using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using ImGuiNET;
using Veldrid;
using Veldrid.Sdl2;
using Veldrid.SPIRV;
using Veldrid.StartupUtilities;

namespace ImguiTest;

public class Program
{
    private static readonly WindowCreateInfo WindowCI = new WindowCreateInfo(
        50,
        50,
        1280,
        720,
        WindowState.Normal,
        "Imgui program"
    );

    private static readonly GraphicsDeviceOptions GDOptions =
        new GraphicsDeviceOptions {
            PreferStandardClipSpaceYDirection = true,
            PreferDepthRangeZeroToOne = true,
            SyncToVerticalBlank = true
        };

    private static Sdl2Window     _window;
    private static GraphicsDevice _gd;
    private static CommandList    _commandList;
    private static IntPtr         _hContext;

    private static DeviceBuffer _vertexBuffer;
    private static DeviceBuffer _indexBuffer;
    private static DeviceBuffer _projMatrixBuffer;

    private static Texture     _fontTexture;
    private static TextureView _fontTextureView;

    private static Shader _vertexShader, _fragmentShader;

    private static ResourceLayout _layout, _textureLayout;

    private static Pipeline _pipeline;

    private static ResourceSet _mainResourceSet, _fontTextureResourceSet;

    private static bool _frameStarted, _showDemoWindow;

    public static float FrameTime;

    public static float FPS => 1f / FrameTime;

    private static ResourceFactory ResourceFactory => _gd.ResourceFactory;

    private static void Initialize()
    {
        Console.WriteLine("Initialization...");

        Console.WriteLine("Creating window & graphics device...");

        VeldridStartup.CreateWindowAndGraphicsDevice(
            WindowCI,
            GDOptions,
            GraphicsBackend.Vulkan,
            out _window,
            out _gd
        );

        _window.Resized += () => { _gd.MainSwapchain.Resize((uint)_window.Width, (uint)_window.Height); };

        _commandList = ResourceFactory.CreateCommandList();

        _hContext = ImGui.CreateContext();

        ImGuiIOPtr io = ImGui.GetIO();
        io.BackendFlags |= ImGuiBackendFlags.RendererHasVtxOffset;
        io.ConfigFlags |= ImGuiConfigFlags.NavEnableKeyboard | ImGuiConfigFlags.DockingEnable;
        io.Fonts.Flags |= ImFontAtlasFlags.NoBakedLines;

        _vertexBuffer = ResourceFactory.CreateBuffer(
            new BufferDescription(10_000, BufferUsage.VertexBuffer | BufferUsage.Dynamic)
        );
        _vertexBuffer.Name = "imgui.net_vertexbuffer";
        _indexBuffer = ResourceFactory.CreateBuffer(
            new BufferDescription(2_000, BufferUsage.IndexBuffer | BufferUsage.Dynamic)
        );
        _indexBuffer.Name = "imgui.net_indexbuffer";

        io.Fonts.GetTexDataAsRGBA32(
            out IntPtr fontTexPixels,
            out int fontTexW,
            out int fontTexH,
            out int fontTexBpp
        );
        io.Fonts.SetTexID(1);

        _fontTexture = ResourceFactory.CreateTexture(
            TextureDescription.Texture2D(
                (uint)fontTexW,
                (uint)fontTexH,
                1,
                1,
                PixelFormat.R8_G8_B8_A8_UNorm,
                TextureUsage.Sampled
            )
        );
        _fontTexture.Name = "imgui.net_fonttexture";
        _gd.UpdateTexture(
            _fontTexture,
            fontTexPixels,
            (uint)(fontTexBpp * fontTexW * fontTexH),
            0,
            0,
            0,
            (uint)fontTexW,
            (uint)fontTexH,
            1,
            0,
            0
        );
        _fontTextureView = ResourceFactory.CreateTextureView(_fontTexture);

        io.Fonts.ClearTexData();

        _projMatrixBuffer = ResourceFactory.CreateBuffer(
            new BufferDescription(8 * 8, BufferUsage.UniformBuffer | BufferUsage.Dynamic)
        );
        _projMatrixBuffer.Name = "imgui.net_projbuffer";

        // _vertexShader = ResourceFactory.CreateShader(
        //     new ShaderDescription(ShaderStages.Vertex, Shaders.VertexShaderBytes, "main")
        // );
        // _fragmentShader = ResourceFactory.CreateShader(
        //     new ShaderDescription(ShaderStages.Fragment, Shaders.FragmentShaderBytes, "main")
        // );

        var shaders = ResourceFactory.CreateFromSpirv(
            new ShaderDescription(ShaderStages.Vertex, Shaders.VertexShaderBytes, "main"),
            new ShaderDescription(ShaderStages.Fragment, Shaders.FragmentShaderBytes, "main")
        );

        _vertexShader   = shaders[0];
        _fragmentShader = shaders[1];

        var vertexLayouts = new[] {
            new VertexLayoutDescription(
                new VertexElementDescription("in_position", VertexElementSemantic.Position, VertexElementFormat.Float2),
                new VertexElementDescription(
                    "in_texCoord",
                    VertexElementSemantic.TextureCoordinate,
                    VertexElementFormat.Float2
                ),
                new VertexElementDescription("in_color", VertexElementSemantic.Color, VertexElementFormat.Byte4_Norm)
            )
        };

        _layout = ResourceFactory.CreateResourceLayout(
            new ResourceLayoutDescription(
                new ResourceLayoutElementDescription(
                    "ProjectionMatrixBuffer",
                    ResourceKind.UniformBuffer,
                    ShaderStages.Vertex
                ),
                new ResourceLayoutElementDescription("MainSampler", ResourceKind.Sampler, ShaderStages.Fragment)
            )
        );
        _textureLayout = ResourceFactory.CreateResourceLayout(
            new ResourceLayoutDescription(
                new ResourceLayoutElementDescription("MainTexture", ResourceKind.TextureReadOnly, ShaderStages.Fragment)
            )
        );

        _pipeline = ResourceFactory.CreateGraphicsPipeline(
            new GraphicsPipelineDescription(
                BlendStateDescription.SingleAlphaBlend,
                new DepthStencilStateDescription(false, false, ComparisonKind.Always),
                new RasterizerStateDescription(
                    FaceCullMode.None,
                    PolygonFillMode.Solid,
                    FrontFace.Clockwise,
                    false,
                    true
                ),
                PrimitiveTopology.TriangleList,
                new ShaderSetDescription(vertexLayouts, new[] {_vertexShader, _fragmentShader}),
                new[] {_layout, _textureLayout},
                _gd.SwapchainFramebuffer.OutputDescription,
                ResourceBindingModel.Default
            )
        );

        _mainResourceSet =
            ResourceFactory.CreateResourceSet(new ResourceSetDescription(_layout, _projMatrixBuffer, _gd.PointSampler));
        _fontTextureResourceSet =
            ResourceFactory.CreateResourceSet(new ResourceSetDescription(_textureLayout, _fontTextureView));
    }

    static void DeInitialize() { }

    static void MainLoop()
    {
        var sw = Stopwatch.StartNew();

        float deltaTime = 0f;

        while (true)
        {
            deltaTime = CalculateDeltaTime(sw.ElapsedTicks);
            FrameTime = deltaTime;
            sw.Restart();

            var inputSnapshot = _window.PumpEvents();

            if (!_window.Exists || _shouldExit)
                break;
            
            Update(deltaTime, inputSnapshot);
            DrawUI();

            _commandList.Begin();
            _commandList.SetFramebuffer(_gd.MainSwapchain.Framebuffer);
            _commandList.ClearColorTarget(0, RgbaFloat.Grey);
            Render();
            _commandList.End();
            _gd.SubmitCommands(_commandList);
            _gd.SwapBuffers();
            
        }
    }

    public static void Main(string[] args)
    {
        Initialize();

        SetFrameData(1f / 60);
        ImGui.NewFrame();
        _frameStarted = true;

        MainLoop();

        _gd.WaitForIdle();

        // free resources
        _vertexBuffer.Dispose();
        _indexBuffer.Dispose();
        _projMatrixBuffer.Dispose();
        _fontTexture.Dispose();
        _fontTextureView.Dispose();
        _vertexShader.Dispose();
        _fragmentShader.Dispose();
        _layout.Dispose();
        _textureLayout.Dispose();
        _pipeline.Dispose();
        _mainResourceSet.Dispose();

        _commandList.Dispose();
        _gd.Dispose();
    }

    private static float   _debugFloatSlider;
    private static bool    _shouldExit;
    private static Vector3 _colorPickerValue = new Vector3(90);
    
    static void DrawUI()
    {
        ImGui.ShowDemoWindow(ref _showDemoWindow);
        
        ImGui.Text("Hello world!");
        ImGui.SliderFloat(
            "float slider",
            ref _debugFloatSlider,
            0f,
            1f,
            _debugFloatSlider.ToString("0.0")
        );
        if (ImGui.Button("Exit"))
            _shouldExit = true;
        
        ImGui.Begin("FPS Counter");
        ImGui.Text($"FrameTime: {FrameTime * 1000:0.00}ms");
        ImGui.Text($"FPS: {FPS:0.00}");
        ImGui.End();

        ImGui.Begin("Mouse position");
        var mousePosition = ImGui.GetMousePos().ToString();
        ImGui.Text("Mouse position: " + mousePosition);
        ImGui.End();

        ImGui.Begin("Controls window");
        if ( ImGui.CollapsingHeader("Some collapse") )
        {
            ImGui.Checkbox("Collapse check", ref _check);
        }
        
        ImGui.End();
    }

    private static bool _check;

    static void SetFrameData(float deltaTime)
    {
        var io = ImGui.GetIO();
        io.DisplaySize = new Vector2(_window.Width, _window.Height);
        io.DisplayFramebufferScale = Vector2.One;
        io.DeltaTime = deltaTime;
    }

    static void Render()
    {
        if (_frameStarted)
        {
            _frameStarted = false;
            ImGui.Render();

            var drawData = ImGui.GetDrawData();

            uint vertexOffsetInVertices = 0;
            uint indexOffsetInElements = 0;

            if (drawData.CmdListsCount == 0)
            {
                return;
            }

            uint totalVBSize = (uint)(drawData.TotalVtxCount * Unsafe.SizeOf<ImDrawVert>());
            if (totalVBSize > _vertexBuffer.SizeInBytes)
            {
                _gd.DisposeWhenIdle(_vertexBuffer);
                _vertexBuffer = _gd.ResourceFactory.CreateBuffer(
                    new BufferDescription((uint)(totalVBSize * 1.5f), BufferUsage.VertexBuffer | BufferUsage.Dynamic)
                );
            }

            uint totalIBSize = (uint)(drawData.TotalIdxCount * sizeof(ushort));
            if (totalIBSize > _indexBuffer.SizeInBytes)
            {
                _gd.DisposeWhenIdle(_indexBuffer);
                _indexBuffer = _gd.ResourceFactory.CreateBuffer(
                    new BufferDescription((uint)(totalIBSize * 1.5f), BufferUsage.IndexBuffer | BufferUsage.Dynamic)
                );
            }

            for (int i = 0; i < drawData.CmdListsCount; i++)
            {
                ImDrawListPtr cmd_list = drawData.CmdListsRange[i];

                _commandList.UpdateBuffer(
                    _vertexBuffer,
                    vertexOffsetInVertices * (uint)Unsafe.SizeOf<ImDrawVert>(),
                    cmd_list.VtxBuffer.Data,
                    (uint)(cmd_list.VtxBuffer.Size * Unsafe.SizeOf<ImDrawVert>())
                );

                _commandList.UpdateBuffer(
                    _indexBuffer,
                    indexOffsetInElements * sizeof(ushort),
                    cmd_list.IdxBuffer.Data,
                    (uint)(cmd_list.IdxBuffer.Size * sizeof(ushort))
                );

                vertexOffsetInVertices += (uint)cmd_list.VtxBuffer.Size;
                indexOffsetInElements += (uint)cmd_list.IdxBuffer.Size;
            }

            // Setup orthographic projection matrix into our constant buffer
            ImGuiIOPtr io = ImGui.GetIO();
            Matrix4x4 mvp = Matrix4x4.CreateOrthographicOffCenter(
                0f,
                io.DisplaySize.X,
                io.DisplaySize.Y,
                0.0f,
                -1.0f,
                1.0f
            );

            _gd.UpdateBuffer(_projMatrixBuffer, 0, ref mvp);

            _commandList.SetVertexBuffer(0, _vertexBuffer);
            _commandList.SetIndexBuffer(_indexBuffer, IndexFormat.UInt16);
            _commandList.SetPipeline(_pipeline);
            _commandList.SetGraphicsResourceSet(0, _mainResourceSet);

            drawData.ScaleClipRects(io.DisplayFramebufferScale);

            // Render command lists
            int vtx_offset = 0;
            int idx_offset = 0;
            for (int n = 0; n < drawData.CmdListsCount; n++)
            {
                ImDrawListPtr cmd_list = drawData.CmdListsRange[n];
                for (int cmd_i = 0; cmd_i < cmd_list.CmdBuffer.Size; cmd_i++)
                {
                    ImDrawCmdPtr pcmd = cmd_list.CmdBuffer[cmd_i];
                    if (pcmd.UserCallback != IntPtr.Zero)
                    {
                        throw new NotImplementedException();
                    }
                    else
                    {
                        if (pcmd.TextureId != IntPtr.Zero)
                        {
                            if (pcmd.TextureId == 1)
                            {
                                _commandList.SetGraphicsResourceSet(1, _fontTextureResourceSet);
                            }
                            /*else
                            {
                                _commandList.SetGraphicsResourceSet(1, GetImageResourceSet(pcmd.TextureId));
                            }*/
                        }

                        _commandList.SetScissorRect(
                            0,
                            (uint)pcmd.ClipRect.X,
                            (uint)pcmd.ClipRect.Y,
                            (uint)(pcmd.ClipRect.Z - pcmd.ClipRect.X),
                            (uint)(pcmd.ClipRect.W - pcmd.ClipRect.Y)
                        );

                        _commandList.DrawIndexed(
                            pcmd.ElemCount,
                            1,
                            pcmd.IdxOffset + (uint)idx_offset,
                            (int)pcmd.VtxOffset + vtx_offset,
                            0
                        );
                    }
                }

                vtx_offset += cmd_list.VtxBuffer.Size;
                idx_offset += cmd_list.IdxBuffer.Size;
            }
        }
    }

    static void Update(float deltaTime, InputSnapshot inputSnapshot)
    {
        if (_frameStarted)
        {
            ImGui.Render();
        }

        SetFrameData(deltaTime);
        UpdateInput(inputSnapshot);

        _frameStarted = true;
        ImGui.NewFrame();
    }

    static void UpdateInput(InputSnapshot snapshot)
    {
        ImGuiIOPtr io = ImGui.GetIO();
        io.AddMousePosEvent(snapshot.MousePosition.X, snapshot.MousePosition.Y);
        io.AddMouseButtonEvent(0, snapshot.IsMouseDown(MouseButton.Left));
        io.AddMouseButtonEvent(1, snapshot.IsMouseDown(MouseButton.Right));
        io.AddMouseButtonEvent(2, snapshot.IsMouseDown(MouseButton.Middle));
        io.AddMouseButtonEvent(3, snapshot.IsMouseDown(MouseButton.Button1));
        io.AddMouseButtonEvent(4, snapshot.IsMouseDown(MouseButton.Button2));
        io.AddMouseWheelEvent(0f, snapshot.WheelDelta);
        for (int i = 0; i < snapshot.KeyCharPresses.Count; i++)
        {
            io.AddInputCharacter(snapshot.KeyCharPresses[i]);
        }

        for (int i = 0; i < snapshot.KeyEvents.Count; i++)
        {
            KeyEvent keyEvent = snapshot.KeyEvents[i];
            if (TryMapKey(keyEvent.Key, out ImGuiKey imguikey))
            {
                io.AddKeyEvent(imguikey, keyEvent.Down);
            }
        }
    }

    static float CalculateDeltaTime(long ticks)
    {
        return (float)ticks / Stopwatch.Frequency;
    }

    static bool TryMapKey(Key key, out ImGuiKey result)
    {
        ImGuiKey KeyToImGuiKeyShortcut(Key keyToConvert, Key startKey1, ImGuiKey startKey2)
        {
            int changeFromStart1 = (int)keyToConvert - (int)startKey1;
            return startKey2 + changeFromStart1;
        }

        result = key switch {
            >= Key.F1 and <= Key.F12 => KeyToImGuiKeyShortcut(key, Key.F1, ImGuiKey.F1),
            >= Key.Keypad0 and <= Key.Keypad9 => KeyToImGuiKeyShortcut(key, Key.Keypad0, ImGuiKey.Keypad0),
            >= Key.A and <= Key.Z => KeyToImGuiKeyShortcut(key, Key.A, ImGuiKey.A),
            >= Key.Number0 and <= Key.Number9 => KeyToImGuiKeyShortcut(key, Key.Number0, ImGuiKey._0),
            Key.ShiftLeft or Key.ShiftRight => ImGuiKey.ModShift,
            Key.ControlLeft or Key.ControlRight => ImGuiKey.ModCtrl,
            Key.AltLeft or Key.AltRight => ImGuiKey.ModAlt,
            Key.WinLeft or Key.WinRight => ImGuiKey.ModSuper,
            Key.Menu => ImGuiKey.Menu,
            Key.Up => ImGuiKey.UpArrow,
            Key.Down => ImGuiKey.DownArrow,
            Key.Left => ImGuiKey.LeftArrow,
            Key.Right => ImGuiKey.RightArrow,
            Key.Enter => ImGuiKey.Enter,
            Key.Escape => ImGuiKey.Escape,
            Key.Space => ImGuiKey.Space,
            Key.Tab => ImGuiKey.Tab,
            Key.BackSpace => ImGuiKey.Backspace,
            Key.Insert => ImGuiKey.Insert,
            Key.Delete => ImGuiKey.Delete,
            Key.PageUp => ImGuiKey.PageUp,
            Key.PageDown => ImGuiKey.PageDown,
            Key.Home => ImGuiKey.Home,
            Key.End => ImGuiKey.End,
            Key.CapsLock => ImGuiKey.CapsLock,
            Key.ScrollLock => ImGuiKey.ScrollLock,
            Key.PrintScreen => ImGuiKey.PrintScreen,
            Key.Pause => ImGuiKey.Pause,
            Key.NumLock => ImGuiKey.NumLock,
            Key.KeypadDivide => ImGuiKey.KeypadDivide,
            Key.KeypadMultiply => ImGuiKey.KeypadMultiply,
            Key.KeypadSubtract => ImGuiKey.KeypadSubtract,
            Key.KeypadAdd => ImGuiKey.KeypadAdd,
            Key.KeypadDecimal => ImGuiKey.KeypadDecimal,
            Key.KeypadEnter => ImGuiKey.KeypadEnter,
            Key.Tilde => ImGuiKey.GraveAccent,
            Key.Minus => ImGuiKey.Minus,
            Key.Plus => ImGuiKey.Equal,
            Key.BracketLeft => ImGuiKey.LeftBracket,
            Key.BracketRight => ImGuiKey.RightBracket,
            Key.Semicolon => ImGuiKey.Semicolon,
            Key.Quote => ImGuiKey.Apostrophe,
            Key.Comma => ImGuiKey.Comma,
            Key.Period => ImGuiKey.Period,
            Key.Slash => ImGuiKey.Slash,
            Key.BackSlash or Key.NonUSBackSlash => ImGuiKey.Backslash,
            _ => ImGuiKey.None
        };

        return result != ImGuiKey.None;
    }
}
