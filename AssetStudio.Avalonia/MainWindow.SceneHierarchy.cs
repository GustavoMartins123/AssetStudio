using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using AssetStudio;
using AssetStudio.Avalonia.ViewModels;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Object = AssetStudio.Object;

namespace AssetStudio.Avalonia;

public partial class MainWindow
{
    internal SceneHierarchyViewModel SceneHierarchy => ViewModel.SceneHierarchy;
    private SceneTreeSearchSession treeSearchSession => SceneHierarchy.SearchSession;

    private GameObjectNode[]? appliedTreeSearchMatches
    {
        get => SceneHierarchy.AppliedMatchesArray;
        set
        {
            if (value == null)
            {
                SceneHierarchy.InvalidateSearchCache();
            }
        }
    }

    internal void ResetSceneTreeSearchState()
    {
        treeSearchSession.Invalidate();
        SceneHierarchy.InvalidateSearchCache();
        treeSearchResults.Clear();
        nextGameObjectSearchIndex = 0;
    }

    private void TreeSearch_TextChanged(object? sender, TextChangedEventArgs e)
    {
        // Latest-wins: a new query supersedes any in-flight traversal and its cached matches.
        SceneHierarchy.SearchQuery = treeSearch.Text ?? string.Empty;
        ResetSceneTreeSearchState();
    }

    private async void TreeSearch_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
        {
            return;
        }

        e.Handled = true;
        var searchText = treeSearch.Text?.Trim();
        if (string.IsNullOrEmpty(searchText))
        {
            return;
        }

        // Reset/reload paths clear treeSearchResults directly; treat that as a cache invalidation.
        if (appliedTreeSearchMatches != null && treeSearchResults.Count != appliedTreeSearchMatches.Length)
        {
            ResetSceneTreeSearchState();
        }

        var roots = sceneTreeNodes;
        SceneHierarchy.BindRootNodesReference(roots);
        if (!SceneHierarchy.TryGetCachedMatches(searchText, out _) && !SceneHierarchy.IsSearching)
        {
            StatusStripUpdate($"Searching scene hierarchy for '{searchText}'...");
        }

        var selectedNode = await SceneHierarchy.SearchNextAsync(searchText);
        if (SceneHierarchy.WasLastSearchSuperseded
            || !ReferenceEquals(roots, sceneTreeNodes)
            || !string.Equals(searchText, treeSearch.Text?.Trim(), StringComparison.Ordinal))
        {
            return;
        }

        var currentMatches = SceneHierarchy.AppliedMatchesArray;
        if (currentMatches != null)
        {
            ApplyTreeSearchMatches(currentMatches);
            nextGameObjectSearchIndex = SceneHierarchy.CurrentMatchIndex;
        }

        if (selectedNode == null)
        {
            if (!string.IsNullOrEmpty(SceneHierarchy.SearchStatusText))
            {
                StatusStripUpdate(SceneHierarchy.SearchStatusText);
            }
            return;
        }

        SceneTreeView.SelectedItem = selectedNode;
        StatusStripUpdate(SceneHierarchy.SearchStatusText);
    }

    private void ApplyTreeSearchMatches(GameObjectNode[] matches)
    {
        if (treeSearchResults.Count == matches.Length
            && (matches.Length == 0 || ReferenceEquals(treeSearchResults[0], matches[0])))
        {
            return;
        }

        treeSearchResults.Clear();
        treeSearchResults.AddRange(matches);
        nextGameObjectSearchIndex = 0;
    }

    private void GoToSceneHierarchy_Click(object? sender, RoutedEventArgs e)
    {
        var item = assetContextItem ?? AssetListDataGrid.SelectedItem as AssetItem;
        if (!SceneHierarchy.TryNavigateToAssetNode(item))
        {
            StatusStripUpdate(SceneHierarchy.SearchStatusText);
            return;
        }

        LeftTabControl.SelectedIndex = 0;
        SceneTreeView.SelectedItem = SceneHierarchy.SelectedNode;
        SceneTreeView.Focus();
    }

    private void LinkAssetItemsToSceneNodes(Dictionary<GameObject, GameObjectNode> treeNodeDictionary, Dictionary<Object, AssetItem> objectAssetItemDic)
    {
        LinkAssetItemsToSceneNodesBackground(assetsManager.assetsFileList, treeNodeDictionary, objectAssetItemDic);
    }
}

/// <summary>
/// Stack-safe scene hierarchy name search (iterative pre-order, same order as the legacy recursive DFS).
/// </summary>
internal static class SceneTreeSearch
{
    private const int CancellationCheckMask = 1023;

    internal static GameObjectNode[] FindMatches(IReadOnlyList<GameObjectNode> roots, string query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(roots);
        ArgumentNullException.ThrowIfNull(query);

        var results = new List<GameObjectNode>();
        var stack = new Stack<GameObjectNode>();
        for (int r = roots.Count - 1; r >= 0; r--)
        {
            stack.Push(roots[r]);
        }

        var visited = 0;
        while (stack.Count > 0)
        {
            if ((++visited & CancellationCheckMask) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            var node = stack.Pop();
            if (node.Name is { } name && name.Contains(query, StringComparison.OrdinalIgnoreCase))
            {
                results.Add(node);
            }

            var children = node.Children;
            for (int i = children.Count - 1; i >= 0; i--)
            {
                stack.Push(children[i]);
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        return results.ToArray();
    }
}

/// <summary>
/// Latest-wins background scene search with a per-(query, tree generation) match cache, so
/// cycling through matches with repeated Enter presses never re-traverses the hierarchy.
/// Tree generation = identity of the root list plus its root count (reload swaps the list,
/// reset clears it in place). Callers on the UI thread; internally synchronized for tests.
/// </summary>
internal sealed class SceneTreeSearchSession
{
    private readonly object gate = new();
    private CancellationTokenSource? activeCts;
    private Task<GameObjectNode[]?>? activeTask;
    private string? activeQuery;
    private IReadOnlyList<GameObjectNode>? activeRoots;
    private int activeRootCount;

    private bool hasCache;
    private string? cachedQuery;
    private IReadOnlyList<GameObjectNode>? cachedRoots;
    private int cachedRootCount;
    private GameObjectNode[] cachedMatches = Array.Empty<GameObjectNode>();

    private int traversalCount;

    /// <summary>Number of full hierarchy traversals started (diagnostics/tests).</summary>
    public int TraversalCount => Volatile.Read(ref traversalCount);

    public bool IsSearching
    {
        get
        {
            lock (gate)
            {
                return activeCts != null;
            }
        }
    }

    public bool TryGetCached(IReadOnlyList<GameObjectNode> roots, string query, out GameObjectNode[] matches)
    {
        lock (gate)
        {
            if (hasCache
                && ReferenceEquals(roots, cachedRoots)
                && roots.Count == cachedRootCount
                && string.Equals(query, cachedQuery, StringComparison.Ordinal))
            {
                matches = cachedMatches;
                return true;
            }
        }

        matches = Array.Empty<GameObjectNode>();
        return false;
    }

    /// <summary>Cancels any in-flight search and drops the cached matches.</summary>
    public void Invalidate()
    {
        lock (gate)
        {
            CancelActiveLocked();
            hasCache = false;
            cachedQuery = null;
            cachedRoots = null;
            cachedRootCount = 0;
            cachedMatches = Array.Empty<GameObjectNode>();
        }
    }

    /// <summary>
    /// Returns the matches for <paramref name="query"/> (cached when possible), or <c>null</c> when
    /// the search was superseded by a newer query or invalidated. A concurrent request for the same
    /// (query, tree generation) joins the in-flight search instead of restarting it.
    /// </summary>
    public Task<GameObjectNode[]?> SearchAsync(IReadOnlyList<GameObjectNode> roots, string query)
    {
        ArgumentNullException.ThrowIfNull(roots);
        ArgumentNullException.ThrowIfNull(query);

        lock (gate)
        {
            if (hasCache
                && ReferenceEquals(roots, cachedRoots)
                && roots.Count == cachedRootCount
                && string.Equals(query, cachedQuery, StringComparison.Ordinal))
            {
                return Task.FromResult<GameObjectNode[]?>(cachedMatches);
            }

            if (activeTask != null
                && ReferenceEquals(roots, activeRoots)
                && roots.Count == activeRootCount
                && string.Equals(query, activeQuery, StringComparison.Ordinal))
            {
                return activeTask;
            }

            CancelActiveLocked();
            var cts = new CancellationTokenSource();
            var snapshot = new GameObjectNode[roots.Count];
            for (int i = 0; i < snapshot.Length; i++)
            {
                snapshot[i] = roots[i];
            }

            activeCts = cts;
            activeQuery = query;
            activeRoots = roots;
            activeRootCount = snapshot.Length;
            var task = RunSearchAsync(cts, snapshot, roots, query);
            if (ReferenceEquals(activeCts, cts))
            {
                activeTask = task;
            }

            return task;
        }
    }

    private async Task<GameObjectNode[]?> RunSearchAsync(
        CancellationTokenSource cts,
        GameObjectNode[] snapshot,
        IReadOnlyList<GameObjectNode> roots,
        string query)
    {
        var token = cts.Token;
        try
        {
            var matches = await Task.Run(() =>
            {
                Interlocked.Increment(ref traversalCount);
                return SceneTreeSearch.FindMatches(snapshot, query, token);
            }, token).ConfigureAwait(true);

            lock (gate)
            {
                if (token.IsCancellationRequested || !ReferenceEquals(activeCts, cts))
                {
                    return null;
                }

                hasCache = true;
                cachedQuery = query;
                cachedRoots = roots;
                cachedRootCount = snapshot.Length;
                cachedMatches = matches;
                return matches;
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            return null;
        }
        finally
        {
            lock (gate)
            {
                if (ReferenceEquals(activeCts, cts))
                {
                    activeCts = null;
                    activeTask = null;
                    activeQuery = null;
                    activeRoots = null;
                    activeRootCount = 0;
                }

                // A cancelled source may still be inside Cancel() on another frame; it owns no
                // timer or wait handle, so only dispose sources that completed uncancelled.
                if (!token.IsCancellationRequested)
                {
                    cts.Dispose();
                }
            }
        }
    }

    private void CancelActiveLocked()
    {
        var cts = activeCts;
        activeCts = null;
        activeTask = null;
        activeQuery = null;
        activeRoots = null;
        activeRootCount = 0;
        cts?.Cancel();
    }
}
