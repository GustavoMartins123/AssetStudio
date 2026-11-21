using System;
using System.Threading;
using System.Threading.Tasks;
using AssetStudio.Avalonia.Services;

namespace AssetStudio.Avalonia.ViewModels;

public sealed class ShaderPreviewViewModel : ViewModelBase, IDisposable
{
    private readonly Action<Action> _dispatchToUi;
    private readonly AsyncPreviewRequestCoordinator _requests = new();
    private Shader? _shader;
    private Material? _material;
    private Func<CancellationToken, Shader>? _resolveShader;
    private Func<Shader, Material?, CancellationToken, ShaderPreviewScene>? _prepareScene;
    private Action? _onOpenMaterial;
    private ShaderPreviewScene? _scene;
    private ShaderPreviewCamera _camera = ShaderPreviewCamera.Default;
    private bool _useMaterial, _isVisible, _isBusy, _isCode, _suspended, _refreshPending;
    private string _status = "", _code = "";
    private ShaderPreviewFrame? _frame;

    internal ShaderPreviewViewModel(Action<Action> dispatchToUi)
    {
        _dispatchToUi = dispatchToUi;
        OpenMaterialCommand = new RelayCommand(OpenMaterial, () => CanOpenMaterial);
        ShowRenderCommand = new RelayCommand(() => SelectCode(false));
        ShowCodeCommand = new RelayCommand(() => SelectCode(true));
        UseDefaultsCommand = new RelayCommand(() => SelectMaterial(false));
        UseMaterialCommand = new RelayCommand(() => SelectMaterial(true));
        MoveCameraCommand = new RelayCommand<string>(MoveCamera, _ => CanAdjustCamera);
    }

    public RelayCommand OpenMaterialCommand { get; }
    public RelayCommand ShowRenderCommand { get; }
    public RelayCommand ShowCodeCommand { get; }
    public RelayCommand UseDefaultsCommand { get; }
    public RelayCommand UseMaterialCommand { get; }
    public RelayCommand<string> MoveCameraCommand { get; }

    public bool IsVisible
    {
        get => _isVisible;
        private set
        {
            if (SetProperty(ref _isVisible, value))
            {
                NotifyMaterial();
            }
        }
    }

    public bool IsCode
    {
        get => _isCode;
        private set
        {
            if (SetProperty(ref _isCode, value))
            {
                NotifyCamera();
            }
        }
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                NotifyCamera();
            }
        }
    }

    public ShaderPreviewFrame? Frame
    {
        get => _frame;
        private set
        {
            if (SetProperty(ref _frame, value))
            {
                NotifyCamera();
            }
        }
    }

    public string Status
    {
        get => _status;
        private set => SetProperty(ref _status, value);
    }

    public string Code
    {
        get => _code;
        private set => SetProperty(ref _code, value);
    }

    public bool HasMaterial => _material != null;
    public bool CanOpenMaterial => HasMaterial && !IsVisible;
    public bool CanAdjustCamera => Frame != null && !IsBusy && !IsCode;
    public bool UsesMaterial => _useMaterial;

    internal void OpenShader(Shader shader, Func<Shader, Material?, CancellationToken, ShaderPreviewScene> prepareScene)
    {
        _shader = shader;
        _prepareScene = prepareScene;
        IsVisible = true;
        Refresh();
    }

    internal void SetMaterial(Material material, Func<CancellationToken, Shader> resolveShader,
        Func<Shader, Material?, CancellationToken, ShaderPreviewScene> prepareScene, Action onOpenMaterial)
    {
        _material = material;
        _resolveShader = resolveShader;
        _prepareScene = prepareScene;
        _onOpenMaterial = onOpenMaterial;
        NotifyMaterial();
    }

    private void OpenMaterial()
    {
        _onOpenMaterial?.Invoke();
        _useMaterial = true;
        OnPropertyChanged(nameof(UsesMaterial));
        IsVisible = true;
        Refresh();
    }

    private void SelectMaterial(bool useMaterial)
    {
        if (!HasMaterial || _useMaterial == useMaterial)
        {
            return;
        }
        _useMaterial = useMaterial;
        OnPropertyChanged(nameof(UsesMaterial));
        _scene = null;
        Frame = null;
        Refresh();
    }

    private void SelectCode(bool showCode)
    {
        if (IsCode != showCode)
        {
            IsCode = showCode;
            Refresh();
        }
    }

    private void MoveCamera(string? direction)
    {
        const float angle = MathF.PI / 12;
        _camera = direction switch
        {
            "left" => _camera with { Yaw = _camera.Yaw - angle },
            "right" => _camera with { Yaw = _camera.Yaw + angle },
            "up" => _camera with { Pitch = Math.Min(_camera.Pitch + angle, 1.45f) },
            "down" => _camera with { Pitch = Math.Max(_camera.Pitch - angle, -1.45f) },
            "in" => _camera with { Distance = Math.Max(_camera.Distance * 0.85f, 1.45f) },
            "out" => _camera with { Distance = Math.Min(_camera.Distance / 0.85f, 12f) },
            "reset" => ShaderPreviewCamera.Default,
            _ => _camera
        };
        Refresh();
    }

    private void Refresh()
    {
        if (_suspended)
        {
            _refreshPending = true;
            return;
        }
        if (!IsVisible || _prepareScene is not { } prepare)
        {
            return;
        }

        CancellationToken token = _requests.BeginNewRequest(out long request);
        Shader? shader = _shader;
        var resolve = _resolveShader;
        Material? material = _useMaterial ? _material : null;
        var scene = _scene;
        var camera = _camera;
        bool showCode = IsCode;
        IsBusy = true;
        Status = showCode ? "Reading shader code…" : "Rendering shader…";
        _ = CompleteAsync();

        async Task CompleteAsync()
        {
            string? code = null;
            ShaderPreviewFrame? frame = null;
            string status;
            try
            {
                await Task.Run(() =>
                {
                    shader ??= resolve?.Invoke(token) ?? throw new InvalidOperationException("The material's shader is unavailable.");
                    if (showCode)
                    {
                        code = PreviewSessionViewModel.TruncatePreviewText(shader.Convert());
                    }
                    else
                    {
                        scene ??= prepare(shader, material, token);
                        frame = D3D11ShaderPreviewRenderer.Render(scene, camera, token);
                    }
                }, token).ConfigureAwait(false);
                status = showCode
                    ? shader!.m_Name
                    : $"{scene!.Pass.ShaderName} · Unity 5.3 Built-in · Direct3D 11 · {material?.m_Name ?? "Shader defaults"}";
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                return;
            }
            catch (Exception error)
            {
                status = $"Shader preview unavailable. {error.Message}";
            }
            _dispatchToUi(() =>
            {
                if (token.IsCancellationRequested || !_requests.IsCurrent(request))
                {
                    return;
                }
                _shader = shader;
                _scene = scene;
                if (showCode)
                {
                    Code = code ?? string.Empty;
                }
                else
                {
                    Frame = frame;
                }
                Status = status;
                IsBusy = false;
            });
        }
    }

    internal void SetSuspended(bool suspended)
    {
        _suspended = suspended;
        if (suspended && IsBusy)
        {
            _requests.CancelCurrent();
            _refreshPending = true;
            IsBusy = false;
        }
        else if (!suspended && _refreshPending)
        {
            _refreshPending = false;
            Refresh();
        }
    }

    internal void Clear()
    {
        _requests.CancelCurrent();
        _shader = null;
        _material = null;
        _resolveShader = null;
        _prepareScene = null;
        _onOpenMaterial = null;
        _scene = null;
        _camera = ShaderPreviewCamera.Default;
        _refreshPending = false;
        _useMaterial = false;
        IsVisible = false;
        IsBusy = false;
        IsCode = false;
        Frame = null;
        Code = "";
        Status = "";
        NotifyMaterial();
        OnPropertyChanged(nameof(UsesMaterial));
    }

    private void NotifyMaterial()
    {
        OnPropertyChanged(nameof(HasMaterial));
        OnPropertyChanged(nameof(CanOpenMaterial));
        OpenMaterialCommand.NotifyCanExecuteChanged();
    }

    private void NotifyCamera()
    {
        OnPropertyChanged(nameof(CanAdjustCamera));
        MoveCameraCommand.NotifyCanExecuteChanged();
    }

    public void Dispose() => Clear();
}
