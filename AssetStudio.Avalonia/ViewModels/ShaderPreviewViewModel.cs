using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AssetStudio.Avalonia.Services;

namespace AssetStudio.Avalonia.ViewModels;

public sealed class ShaderPreviewViewModel : ViewModelBase, IDisposable
{
    private readonly Action<Action> _dispatchToUi;
    private readonly AsyncPreviewRequestCoordinator _requests = new();
    private Func<CancellationToken, ShaderPreviewScene>? _prepare;
    private ShaderPreviewScene? _scene;
    private D3D11ShaderPreviewRenderer? _renderer;
    private ShaderPreviewCamera _camera;
    private float _time, _deltaTime;
    private bool _active, _isAnimated, _busy, _suspended, _pending;
    private bool _resetMotionHistory;
    private ShaderPreviewFrame? _frame;
    private string? _status, _error, _passDetails;

    internal ShaderPreviewViewModel(Action<Action> dispatchToUi) => _dispatchToUi = dispatchToUi;

    public bool IsActive
    {
        get => _active;
        private set => SetProperty(ref _active, value);
    }

    public ShaderPreviewFrame? Frame
    {
        get => _frame;
        private set => SetProperty(ref _frame, value);
    }

    internal bool IsAnimated => _isAnimated;
    internal string? Status
    {
        get => _status;
        private set => SetProperty(ref _status, value);
    }

    internal string? Error
    {
        get => _error;
        private set => SetProperty(ref _error, value);
    }

    internal string? PassDetails => _passDetails;

    internal void Open(Func<CancellationToken, ShaderPreviewScene> prepare)
    {
        Clear();
        _prepare = prepare;
        _camera = ShaderPreviewCamera.Default;
        IsActive = true;
        QueueFrame();
    }

    internal void UpdateCamera(ShaderPreviewCamera camera)
    {
        if (camera != _camera)
        {
            _camera = camera;
            QueueFrame();
        }
    }

    internal void UpdateTime(int frame, float framesPerSecond)
    {
        _time = frame / framesPerSecond;
        _deltaTime = 1 / framesPerSecond;
        _resetMotionHistory |= frame == 0;
        QueueFrame();
    }

    private void QueueFrame()
    {
        _pending = true;
        RenderPendingFrame();
    }

    private void RenderPendingFrame()
    {
        if (_busy || _suspended || !_pending || _prepare is not { } prepare)
        {
            return;
        }

        _busy = true;
        _pending = false;
        var scene = _scene;
        var renderer = _renderer;
        _renderer = null; // The request owns the renderer until its UI continuation completes.
        var camera = _camera;
        float time = _time, deltaTime = _deltaTime;
        bool resetMotionHistory = _resetMotionHistory;
        _resetMotionHistory = false;
        CancellationToken token = _requests.BeginNewRequest(out long request);
        _ = RenderAsync();

        async Task RenderAsync()
        {
            ShaderPreviewFrame frame;
            try
            {
                frame = await Task.Run(() =>
                {
                    scene ??= prepare(token);
                    renderer ??= new D3D11ShaderPreviewRenderer(scene);
                    return renderer.Render(camera, token, time, deltaTime, resetMotionHistory);
                }, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                renderer?.Dispose();
                return;
            }
            catch (Exception error)
            {
                renderer?.Dispose();
                _dispatchToUi(() =>
                {
                    if (!_requests.IsCurrent(request))
                    {
                        return;
                    }
                    _prepare = null;
                    _pending = false;
                    _busy = false;
                    Frame = null;
                    Error = $"Shader preview unavailable. {error.Message}";
                });
                return;
            }

            _dispatchToUi(() =>
            {
                if (!_requests.IsCurrent(request))
                {
                    renderer?.Dispose();
                    return;
                }

                _renderer = renderer;
                bool first = _scene == null;
                _scene = scene;
                if (first)
                {
                    _isAnimated = scene!.IsAnimated;
                    _passDetails = string.Join(Environment.NewLine, scene.Operations.Select(operation => operation switch
                    {
                        ShaderPreviewDraw draw => $"{draw.Pass.ShaderName} · {draw.Pass.PassName} {draw.Pass.LightMode} [{string.Join(", ", draw.Pass.Keywords)}]",
                        ShaderPreviewGrab grab => $"GrabPass · {grab.TextureName}",
                        ShaderPreviewDispatch dispatch => $"Compute · {dispatch.Kernel.ShaderName}/{dispatch.Kernel.Name}",
                        ShaderPreviewCopyMip copy => $"Copy mip {copy.Mip} · {copy.Destination.Name}",
                        ShaderPreviewPresentSlice present => $"Display slice {present.Slice} · {present.Source.Name}",
                        _ => throw new InvalidOperationException("Unknown shader preview operation.")
                    }));
                    if (scene.UnusedMaterialKeywords.Count > 0)
                    {
                        _passDetails += Environment.NewLine + $"Keywords unused by this shader: {string.Join(", ", scene.UnusedMaterialKeywords)}";
                    }
                }
                Frame = frame;
                if (first)
                {
                    Status = scene!.ShaderName;
                }
                _busy = false;
                RenderPendingFrame();
            });
        }
    }

    internal void SetSuspended(bool suspended)
    {
        _suspended = suspended;
        RenderPendingFrame();
    }

    internal void Clear()
    {
        _requests.CancelCurrent();
        _prepare = null;
        _scene = null;
        _renderer?.Dispose();
        _renderer = null;
        _time = _deltaTime = 0;
        _resetMotionHistory = false;
        _isAnimated = _pending = _busy = false;
        Frame = null;
        Status = Error = _passDetails = null;
        IsActive = false;
    }

    public void Dispose()
    {
        Clear();
        _requests.Dispose();
    }
}
