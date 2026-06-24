// FractalTree — recursive fractal tree on raw Win32 + WGL + OpenGL 3.3 core.
// Zero external dependencies.
//
//   Recursion builds ~16k line segments into one VBO. Growth, wind and the depth
//   color gradient run on the GPU. The canopy tips carry twinkling blossom
//   sprites, and the whole scene is rendered into an HDR buffer and run through a
//   separable-Gaussian bloom for the glow.
//
//   dotnet run -c Release
//
// Author: portfolio piece, no-library style (P/Invoke only).

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;

internal static class Program
{
    const int MaxDepth = 12;
    const float Ratio = 0.75f;
    const float SpreadDeg = 23.0f;
    const float MinLen = 0.0008f;

    const int SIM = 1440;          // square HDR scene resolution
    const int BLOOM = 720;           // bloom buffer resolution (half)

    static readonly List<float> Verts = new();   // x, y, depth, sway
    static readonly List<float> Blossoms = new();    // x, y, phase
    static int VertCount, BlossomCount;

    static int _w = 1080, _h = 1080;
    static bool _running = true;
    static Win.WndProc _wndProcRef;

    [STAThread]
    static void Main()
    {
        BuildTree();

        IntPtr hInstance = Win.GetModuleHandleW(IntPtr.Zero);
        const string cls = "FractalTreeGLWindow";

        _wndProcRef = WindowProc;
        var wc = new Win.WNDCLASSEX
        {
            cbSize = (uint)Marshal.SizeOf<Win.WNDCLASSEX>(),
            style = Win.CS_OWNDC | Win.CS_HREDRAW | Win.CS_VREDRAW,
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProcRef),
            hInstance = hInstance,
            hCursor = Win.LoadCursorW(IntPtr.Zero, (IntPtr)Win.IDC_ARROW),
            lpszClassName = cls,
        };
        if (Win.RegisterClassExW(ref wc) == 0)
            throw new Exception("RegisterClassEx failed: " + Marshal.GetLastWin32Error());

        IntPtr hwnd = Win.CreateWindowExW(
            0, cls, "Recursive Fractal Tree — C#/OpenGL",
            Win.WS_OVERLAPPEDWINDOW | Win.WS_VISIBLE,
            Win.CW_USEDEFAULT, Win.CW_USEDEFAULT, _w, _h,
            IntPtr.Zero, IntPtr.Zero, hInstance, IntPtr.Zero);
        if (hwnd == IntPtr.Zero)
            throw new Exception("CreateWindowEx failed: " + Marshal.GetLastWin32Error());

        IntPtr hdc = Win.GetDC(hwnd);
        IntPtr ctx = CreateGLContext(hdc);
        GL.Load();
        if (GL.wglSwapIntervalEXT != null) GL.wglSwapIntervalEXT(1);

        if (Win.GetClientRect(hwnd, out var rc)) { _w = rc.right - rc.left; _h = rc.bottom - rc.top; }

        uint gradProg = BuildProgram(QuadVS, GradFS);
        uint treeProg = BuildProgram(TreeVS, TreeFS);
        uint blossomProg = BuildProgram(BlossomVS, BlossomFS);
        uint brightProg = BuildProgram(QuadVS, BrightFS);
        uint blurProg = BuildProgram(QuadVS, BlurFS);
        uint compositeProg = BuildProgram(QuadVS, CompositeFS);

        int tTime = GL.glGetUniformLocation(treeProg, Ascii("uTime"));
        int tAspect = GL.glGetUniformLocation(treeProg, Ascii("uAspect"));
        int tGrowth = GL.glGetUniformLocation(treeProg, Ascii("uGrowth"));

        int bTime = GL.glGetUniformLocation(blossomProg, Ascii("uTime"));
        int bGrowth = GL.glGetUniformLocation(blossomProg, Ascii("uGrowth"));
        int bSize = GL.glGetUniformLocation(blossomProg, Ascii("uSize"));

        int gScale = GL.glGetUniformLocation(gradProg, Ascii("uScale"));
        int brScale = GL.glGetUniformLocation(brightProg, Ascii("uScale"));
        int brTex = GL.glGetUniformLocation(brightProg, Ascii("uTex"));
        int blScale = GL.glGetUniformLocation(blurProg, Ascii("uScale"));
        int blTex = GL.glGetUniformLocation(blurProg, Ascii("uTex"));
        int blDir = GL.glGetUniformLocation(blurProg, Ascii("uDir"));
        int cScale = GL.glGetUniformLocation(compositeProg, Ascii("uScale"));
        int cScene = GL.glGetUniformLocation(compositeProg, Ascii("uScene"));
        int cBloom = GL.glGetUniformLocation(compositeProg, Ascii("uBloom"));

        uint treeVao = MakeTreeVao();
        uint blossomVao = MakeBlossomVao();
        uint quadVao = MakeQuadVao();

        MakeFbo(SIM, SIM, out uint sceneFbo, out uint sceneTex);
        MakeFbo(BLOOM, BLOOM, out uint bloomFboA, out uint bloomTexA);
        MakeFbo(BLOOM, BLOOM, out uint bloomFboB, out uint bloomTexB);

        GL.glEnable(GL.GL_VERTEX_PROGRAM_POINT_SIZE);
        GL.glLineWidth(1.15f);

        var clock = Stopwatch.StartNew();

        while (_running)
        {
            while (Win.PeekMessageW(out var msg, IntPtr.Zero, 0, 0, Win.PM_REMOVE))
            {
                if (msg.message == Win.WM_QUIT) { _running = false; break; }
                Win.TranslateMessage(ref msg);
                Win.DispatchMessageW(ref msg);
            }
            if (!_running) break;

            float t = (float)clock.Elapsed.TotalSeconds;
            float growth = Math.Min(1.0f, (t % 16.0f) / 5.0f);

            // --- scene into HDR buffer ---
            GL.glBindFramebuffer(GL.GL_FRAMEBUFFER, sceneFbo);
            GL.glViewport(0, 0, SIM, SIM);
            GL.glDisable(GL.GL_BLEND);

            GL.glUseProgram(gradProg);
            GL.glUniform2f(gScale, 1f, 1f);
            GL.glBindVertexArray(quadVao);
            GL.glDrawArrays(GL.GL_TRIANGLE_STRIP, 0, 4);

            GL.glEnable(GL.GL_BLEND);
            GL.glBlendFunc(GL.GL_SRC_ALPHA, GL.GL_ONE);

            GL.glUseProgram(treeProg);
            GL.glUniform1f(tTime, t);
            GL.glUniform1f(tAspect, 1f);
            GL.glUniform1f(tGrowth, growth);
            GL.glBindVertexArray(treeVao);
            GL.glDrawArrays(GL.GL_LINES, 0, VertCount);

            GL.glUseProgram(blossomProg);
            GL.glUniform1f(bTime, t);
            GL.glUniform1f(bGrowth, growth);
            GL.glUniform1f(bSize, SIM / 240f);
            GL.glBindVertexArray(blossomVao);
            GL.glDrawArrays(GL.GL_POINTS, 0, BlossomCount);

            // --- bloom: bright pass + separable blur ---
            GL.glDisable(GL.GL_BLEND);

            GL.glBindFramebuffer(GL.GL_FRAMEBUFFER, bloomFboA);
            GL.glViewport(0, 0, BLOOM, BLOOM);
            GL.glUseProgram(brightProg);
            GL.glUniform2f(brScale, 1f, 1f);
            GL.glUniform1i(brTex, 0);
            BindTex(0, sceneTex);
            GL.glBindVertexArray(quadVao);
            GL.glDrawArrays(GL.GL_TRIANGLE_STRIP, 0, 4);

            GL.glBindFramebuffer(GL.GL_FRAMEBUFFER, bloomFboB);
            GL.glUseProgram(blurProg);
            GL.glUniform2f(blScale, 1f, 1f);
            GL.glUniform1i(blTex, 0);
            GL.glUniform2f(blDir, 1f / BLOOM, 0f);
            BindTex(0, bloomTexA);
            GL.glDrawArrays(GL.GL_TRIANGLE_STRIP, 0, 4);

            GL.glBindFramebuffer(GL.GL_FRAMEBUFFER, bloomFboA);
            GL.glUniform2f(blDir, 0f, 1f / BLOOM);
            BindTex(0, bloomTexB);
            GL.glDrawArrays(GL.GL_TRIANGLE_STRIP, 0, 4);

            // --- composite to window ---
            GL.glBindFramebuffer(GL.GL_FRAMEBUFFER, 0);
            GL.glViewport(0, 0, _w, _h);
            GL.glClearColor(0f, 0f, 0f, 1f);
            GL.glClear(GL.GL_COLOR_BUFFER_BIT);

            GL.glUseProgram(compositeProg);
            float a = (float)_w / _h;
            if (a >= 1f) GL.glUniform2f(cScale, 1f / a, 1f);
            else GL.glUniform2f(cScale, 1f, a);
            GL.glUniform1i(cScene, 0);
            GL.glUniform1i(cBloom, 1);
            BindTex(0, sceneTex);
            BindTex(1, bloomTexA);
            GL.glBindVertexArray(quadVao);
            GL.glDrawArrays(GL.GL_TRIANGLE_STRIP, 0, 4);

            Win.SwapBuffers(hdc);
        }

        Win.wglMakeCurrent(IntPtr.Zero, IntPtr.Zero);
        Win.wglDeleteContext(ctx);
        Win.ReleaseDC(hwnd, hdc);
    }

    static void BindTex(int unit, uint tex)
    {
        GL.glActiveTexture(GL.GL_TEXTURE0 + (uint)unit);
        GL.glBindTexture(GL.GL_TEXTURE_2D, tex);
    }

    static IntPtr WindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case Win.WM_SIZE:
                int lp = (int)(long)lParam;
                _w = Math.Max(1, lp & 0xFFFF);
                _h = Math.Max(1, (lp >> 16) & 0xFFFF);
                return IntPtr.Zero;
            case Win.WM_DESTROY:
                Win.PostQuitMessage(0);
                return IntPtr.Zero;
        }
        return Win.DefWindowProcW(hWnd, msg, wParam, lParam);
    }

    // ---- tree generation -------------------------------------------------
    static void BuildTree()
    {
        var rng = new Random(7);
        float spread = SpreadDeg * MathF.PI / 180f;

        void Branch(float x, float y, float ang, float len, int depth)
        {
            if (depth > MaxDepth || len < MinLen)
            {
                // leaf tip -> blossom
                Blossoms.Add(x); Blossoms.Add(y);
                Blossoms.Add((float)(rng.NextDouble() * 6.2831));
                Blossoms.Add(MathF.Pow((float)depth / MaxDepth, 1.5f));   // sway weight (matches tip)
                return;
            }
            float nx = x + MathF.Cos(ang) * len;
            float ny = y + MathF.Sin(ang) * len;
            AddVert(x, y, depth);
            AddVert(nx, ny, depth + 1);

            float jL = (float)(rng.NextDouble() - 0.5) * 0.16f;
            float jR = (float)(rng.NextDouble() - 0.5) * 0.16f;
            float lean = 0.04f;

            Branch(nx, ny, ang + spread + jL + lean, len * Ratio, depth + 1);
            Branch(nx, ny, ang - spread + jR + lean, len * Ratio, depth + 1);
        }

        Branch(0f, 0f, MathF.PI / 2f, 1.0f, 0);
        Normalize();
    }

    static void AddVert(float x, float y, int depth)
    {
        float d = (float)depth / MaxDepth;
        float sway = MathF.Pow(d, 1.5f);
        Verts.Add(x); Verts.Add(y); Verts.Add(d); Verts.Add(sway);
    }

    static void Normalize()
    {
        float minX = float.MaxValue, maxX = float.MinValue;
        float minY = float.MaxValue, maxY = float.MinValue;
        for (int i = 0; i < Verts.Count; i += 4)
        {
            float x = Verts[i], y = Verts[i + 1];
            if (x < minX) minX = x; if (x > maxX) maxX = x;
            if (y < minY) minY = y; if (y > maxY) maxY = y;
        }
        float cx = (minX + maxX) * 0.5f, cy = (minY + maxY) * 0.5f;
        float scale = 1.9f / MathF.Max(maxX - minX, maxY - minY);
        for (int i = 0; i < Verts.Count; i += 4)
        {
            Verts[i] = (Verts[i] - cx) * scale;
            Verts[i + 1] = (Verts[i + 1] - cy) * scale;
        }
        for (int i = 0; i < Blossoms.Count; i += 4)
        {
            Blossoms[i] = (Blossoms[i] - cx) * scale;
            Blossoms[i + 1] = (Blossoms[i + 1] - cy) * scale;
        }
        VertCount = Verts.Count / 4;
        BlossomCount = Blossoms.Count / 4;
    }

    // ---- GPU helpers -----------------------------------------------------
    static uint MakeTreeVao()
    {
        uint vao = 0, vbo = 0;
        GL.glGenVertexArrays(1, ref vao);
        GL.glBindVertexArray(vao);
        GL.glGenBuffers(1, ref vbo);
        GL.glBindBuffer(GL.GL_ARRAY_BUFFER, vbo);
        float[] data = Verts.ToArray();
        GCHandle h = GCHandle.Alloc(data, GCHandleType.Pinned);
        try
        {
            GL.glBufferData(GL.GL_ARRAY_BUFFER, (IntPtr)(data.Length * sizeof(float)),
                            h.AddrOfPinnedObject(), GL.GL_STATIC_DRAW);
        }
        finally { h.Free(); }
        int stride = 4 * sizeof(float);
        GL.glVertexAttribPointer(0, 2, GL.GL_FLOAT, 0, stride, (IntPtr)0);
        GL.glEnableVertexAttribArray(0);
        GL.glVertexAttribPointer(1, 1, GL.GL_FLOAT, 0, stride, (IntPtr)(2 * sizeof(float)));
        GL.glEnableVertexAttribArray(1);
        GL.glVertexAttribPointer(2, 1, GL.GL_FLOAT, 0, stride, (IntPtr)(3 * sizeof(float)));
        GL.glEnableVertexAttribArray(2);
        return vao;
    }

    static uint MakeBlossomVao()
    {
        uint vao = 0, vbo = 0;
        GL.glGenVertexArrays(1, ref vao);
        GL.glBindVertexArray(vao);
        GL.glGenBuffers(1, ref vbo);
        GL.glBindBuffer(GL.GL_ARRAY_BUFFER, vbo);
        float[] data = Blossoms.ToArray();
        GCHandle h = GCHandle.Alloc(data, GCHandleType.Pinned);
        try
        {
            GL.glBufferData(GL.GL_ARRAY_BUFFER, (IntPtr)(data.Length * sizeof(float)),
                            h.AddrOfPinnedObject(), GL.GL_STATIC_DRAW);
        }
        finally { h.Free(); }
        int stride = 4 * sizeof(float);
        GL.glVertexAttribPointer(0, 2, GL.GL_FLOAT, 0, stride, (IntPtr)0);
        GL.glEnableVertexAttribArray(0);
        GL.glVertexAttribPointer(1, 1, GL.GL_FLOAT, 0, stride, (IntPtr)(2 * sizeof(float)));
        GL.glEnableVertexAttribArray(1);
        GL.glVertexAttribPointer(2, 1, GL.GL_FLOAT, 0, stride, (IntPtr)(3 * sizeof(float)));
        GL.glEnableVertexAttribArray(2);
        return vao;
    }

    static uint MakeQuadVao()
    {
        float[] q =
        {
            -1f, -1f, 0f, 0f,
             1f, -1f, 1f, 0f,
            -1f,  1f, 0f, 1f,
             1f,  1f, 1f, 1f,
        };
        uint vao = 0, vbo = 0;
        GL.glGenVertexArrays(1, ref vao);
        GL.glBindVertexArray(vao);
        GL.glGenBuffers(1, ref vbo);
        GL.glBindBuffer(GL.GL_ARRAY_BUFFER, vbo);
        GCHandle h = GCHandle.Alloc(q, GCHandleType.Pinned);
        try
        {
            GL.glBufferData(GL.GL_ARRAY_BUFFER, (IntPtr)(q.Length * sizeof(float)),
                            h.AddrOfPinnedObject(), GL.GL_STATIC_DRAW);
        }
        finally { h.Free(); }
        int stride = 4 * sizeof(float);
        GL.glVertexAttribPointer(0, 2, GL.GL_FLOAT, 0, stride, (IntPtr)0);
        GL.glEnableVertexAttribArray(0);
        GL.glVertexAttribPointer(1, 2, GL.GL_FLOAT, 0, stride, (IntPtr)(2 * sizeof(float)));
        GL.glEnableVertexAttribArray(1);
        return vao;
    }

    static void MakeFbo(int w, int h, out uint fbo, out uint tex)
    {
        tex = 0;
        GL.glGenTextures(1, ref tex);
        GL.glActiveTexture(GL.GL_TEXTURE0);
        GL.glBindTexture(GL.GL_TEXTURE_2D, tex);
        GL.glTexImage2D(GL.GL_TEXTURE_2D, 0, (int)GL.GL_RGBA16F, w, h, 0,
                        GL.GL_RGBA, GL.GL_FLOAT, IntPtr.Zero);
        GL.glTexParameteri(GL.GL_TEXTURE_2D, GL.GL_TEXTURE_MIN_FILTER, (int)GL.GL_LINEAR);
        GL.glTexParameteri(GL.GL_TEXTURE_2D, GL.GL_TEXTURE_MAG_FILTER, (int)GL.GL_LINEAR);
        GL.glTexParameteri(GL.GL_TEXTURE_2D, GL.GL_TEXTURE_WRAP_S, (int)GL.GL_CLAMP_TO_EDGE);
        GL.glTexParameteri(GL.GL_TEXTURE_2D, GL.GL_TEXTURE_WRAP_T, (int)GL.GL_CLAMP_TO_EDGE);

        fbo = 0;
        GL.glGenFramebuffers(1, ref fbo);
        GL.glBindFramebuffer(GL.GL_FRAMEBUFFER, fbo);
        GL.glFramebufferTexture2D(GL.GL_FRAMEBUFFER, GL.GL_COLOR_ATTACHMENT0, GL.GL_TEXTURE_2D, tex, 0);
        if (GL.glCheckFramebufferStatus(GL.GL_FRAMEBUFFER) != GL.GL_FRAMEBUFFER_COMPLETE)
            throw new Exception("Framebuffer incomplete");
        GL.glClearColor(0f, 0f, 0f, 1f);
        GL.glViewport(0, 0, w, h);
        GL.glClear(GL.GL_COLOR_BUFFER_BIT);
        GL.glBindFramebuffer(GL.GL_FRAMEBUFFER, 0);
    }

    // ---- shaders ---------------------------------------------------------
    const string QuadVS = @"#version 330 core
layout(location=0) in vec2 aPos;
layout(location=1) in vec2 aUV;
uniform vec2 uScale;
out vec2 vUV;
void main(){ vUV = aUV; gl_Position = vec4(aPos * uScale, 0.0, 1.0); }";

    const string GradFS = @"#version 330 core
in vec2 vUV;
out vec4 FragColor;
void main(){
    float d = length(vUV - vec2(0.5, 0.62));
    vec3 c = mix(vec3(0.03, 0.03, 0.07), vec3(0.005, 0.005, 0.012), smoothstep(0.0, 0.8, d));
    FragColor = vec4(c, 1.0);
}";

    const string TreeVS = @"#version 330 core
layout(location=0) in vec2  aPos;
layout(location=1) in float aDepth;
layout(location=2) in float aSway;
uniform float uTime;
uniform float uAspect;
uniform float uGrowth;
out float vDepth;
out float vAlpha;
void main(){
    float amp  = 0.035;
    float sway = sin(uTime*1.4 + aPos.y*1.6 + aPos.x*0.6) * aSway * amp;
    vec2 p = vec2(aPos.x + sway, aPos.y);
    if (uAspect >= 1.0) p.x /= uAspect; else p.y *= uAspect;
    gl_Position = vec4(p, 0.0, 1.0);
    vDepth = aDepth;
    vAlpha = clamp((uGrowth*1.08 - aDepth) * 6.0, 0.0, 1.0);
}";

    const string TreeFS = @"#version 330 core
in float vDepth;
in float vAlpha;
out vec4 FragColor;
void main(){
    vec3 trunk = vec3(0.30, 0.08, 0.55);
    vec3 mid   = vec3(0.70, 0.16, 0.90);
    vec3 tip   = vec3(1.10, 0.45, 1.00);
    vec3 c = mix(trunk, mid, smoothstep(0.0, 0.5, vDepth));
    c = mix(c, tip, smoothstep(0.5, 1.0, vDepth));
    FragColor = vec4(c, vAlpha * 0.9);
}";

    const string BlossomVS = @"#version 330 core
layout(location=0) in vec2  aPos;
layout(location=1) in float aPhase;
layout(location=2) in float aSway;
uniform float uTime;
uniform float uGrowth;
uniform float uSize;
out float vTw;
void main(){
    float amp  = 0.035;
    float sway = sin(uTime*1.4 + aPos.y*1.6 + aPos.x*0.6) * aSway * amp;
    gl_Position = vec4(aPos.x + sway, aPos.y, 0.0, 1.0);
    float tw = 0.5 + 0.5 * sin(uTime * 3.0 + aPhase);
    gl_PointSize = uSize * (0.6 + 0.8 * tw);
    float reveal = clamp((uGrowth - 0.85) / 0.15, 0.0, 1.0);
    vTw = tw * reveal;
}";

    const string BlossomFS = @"#version 330 core
in float vTw;
out vec4 FragColor;
void main(){
    vec2 d = gl_PointCoord - 0.5;
    float r = length(d) * 2.0;
    float a = smoothstep(1.0, 0.0, r);
    vec3 col = mix(vec3(1.0, 0.55, 0.95), vec3(1.0), 0.5);
    FragColor = vec4(col, a * vTw * 0.8);
}";

    const string BrightFS = @"#version 330 core
in vec2 vUV;
uniform sampler2D uTex;
out vec4 FragColor;
void main(){
    vec3 c = texture(uTex, vUV).rgb;
    vec3 b = max(c - vec3(0.55), vec3(0.0));
    FragColor = vec4(b, 1.0);
}";

    const string BlurFS = @"#version 330 core
in vec2 vUV;
uniform sampler2D uTex;
uniform vec2 uDir;
out vec4 FragColor;
void main(){
    float w[5] = float[](0.227, 0.194, 0.121, 0.054, 0.016);
    vec3 c = texture(uTex, vUV).rgb * w[0];
    for (int i = 1; i < 5; i++){
        c += texture(uTex, vUV + uDir * float(i)).rgb * w[i];
        c += texture(uTex, vUV - uDir * float(i)).rgb * w[i];
    }
    FragColor = vec4(c, 1.0);
}";

    const string CompositeFS = @"#version 330 core
in vec2 vUV;
uniform sampler2D uScene;
uniform sampler2D uBloom;
out vec4 FragColor;
void main(){
    vec3 c = texture(uScene, vUV).rgb + texture(uBloom, vUV).rgb * 1.4;
    c = c / (c + vec3(0.8));
    float vig = smoothstep(1.2, 0.3, length(vUV - 0.5) * 1.35);
    c *= mix(0.6, 1.0, vig);
    FragColor = vec4(pow(c, vec3(0.85)), 1.0);
}";

    static uint BuildProgram(string vsSrc, string fsSrc)
    {
        uint vs = Compile(GL.GL_VERTEX_SHADER, vsSrc);
        uint fs = Compile(GL.GL_FRAGMENT_SHADER, fsSrc);
        uint p = GL.glCreateProgram();
        GL.glAttachShader(p, vs);
        GL.glAttachShader(p, fs);
        GL.glLinkProgram(p);
        int ok = 0; GL.glGetProgramiv(p, GL.GL_LINK_STATUS, ref ok);
        if (ok == 0)
        {
            var log = new byte[2048]; int len = 0;
            GL.glGetProgramInfoLog(p, log.Length, ref len, log);
            throw new Exception("Link error: " + System.Text.Encoding.ASCII.GetString(log, 0, len));
        }
        GL.glDeleteShader(vs); GL.glDeleteShader(fs);
        return p;
    }

    static uint Compile(uint type, string src)
    {
        uint sh = GL.glCreateShader(type);
        IntPtr str = Marshal.StringToHGlobalAnsi(src);
        try { GL.glShaderSource(sh, 1, new[] { str }, IntPtr.Zero); }
        finally { Marshal.FreeHGlobal(str); }
        GL.glCompileShader(sh);
        int ok = 0; GL.glGetShaderiv(sh, GL.GL_COMPILE_STATUS, ref ok);
        if (ok == 0)
        {
            var log = new byte[2048]; int len = 0;
            GL.glGetShaderInfoLog(sh, log.Length, ref len, log);
            throw new Exception("Compile error: " + System.Text.Encoding.ASCII.GetString(log, 0, len));
        }
        return sh;
    }

    static byte[] Ascii(string s)
    {
        var b = new byte[s.Length + 1];
        System.Text.Encoding.ASCII.GetBytes(s, 0, s.Length, b, 0);
        return b;
    }

    static IntPtr CreateGLContext(IntPtr hdc)
    {
        var pfd = new Win.PIXELFORMATDESCRIPTOR
        {
            nSize = (ushort)Marshal.SizeOf<Win.PIXELFORMATDESCRIPTOR>(),
            nVersion = 1,
            dwFlags = Win.PFD_DRAW_TO_WINDOW | Win.PFD_SUPPORT_OPENGL | Win.PFD_DOUBLEBUFFER,
            iPixelType = Win.PFD_TYPE_RGBA,
            cColorBits = 32,
            cDepthBits = 24,
            cStencilBits = 8,
            iLayerType = Win.PFD_MAIN_PLANE,
        };
        int fmt = Win.ChoosePixelFormat(hdc, ref pfd);
        if (fmt == 0) throw new Exception("ChoosePixelFormat failed");
        if (!Win.SetPixelFormat(hdc, fmt, ref pfd)) throw new Exception("SetPixelFormat failed");

        IntPtr tmp = Win.wglCreateContext(hdc);
        Win.wglMakeCurrent(hdc, tmp);

        IntPtr proc = Win.wglGetProcAddress("wglCreateContextAttribsARB");
        if (proc != IntPtr.Zero)
        {
            var create = Marshal.GetDelegateForFunctionPointer<GL.WglCreateContextAttribsARB>(proc);
            int[] attribs = { 0x2091, 3, 0x2092, 3, 0x9126, 0x0001, 0 };
            IntPtr core = create(hdc, IntPtr.Zero, attribs);
            if (core != IntPtr.Zero)
            {
                Win.wglMakeCurrent(hdc, core);
                Win.wglDeleteContext(tmp);
                return core;
            }
        }
        return tmp;
    }
}

// =========================================================================
//  OpenGL entry points
// =========================================================================
internal static class GL
{
    public const uint GL_COLOR_BUFFER_BIT = 0x4000;
    public const uint GL_FLOAT = 0x1406;
    public const uint GL_ARRAY_BUFFER = 0x8892;
    public const uint GL_STATIC_DRAW = 0x88E4;
    public const uint GL_VERTEX_SHADER = 0x8B31;
    public const uint GL_FRAGMENT_SHADER = 0x8B30;
    public const uint GL_COMPILE_STATUS = 0x8B81;
    public const uint GL_LINK_STATUS = 0x8B82;
    public const uint GL_LINES = 0x0001;
    public const uint GL_POINTS = 0x0000;
    public const uint GL_TRIANGLE_STRIP = 0x0005;
    public const uint GL_BLEND = 0x0BE2;
    public const uint GL_SRC_ALPHA = 0x0302;
    public const uint GL_ONE = 1;
    public const uint GL_TEXTURE_2D = 0x0DE1;
    public const uint GL_TEXTURE0 = 0x84C0;
    public const uint GL_RGBA = 0x1908;
    public const uint GL_RGBA16F = 0x881A;
    public const uint GL_LINEAR = 0x2601;
    public const uint GL_CLAMP_TO_EDGE = 0x812F;
    public const uint GL_TEXTURE_MIN_FILTER = 0x2801;
    public const uint GL_TEXTURE_MAG_FILTER = 0x2800;
    public const uint GL_TEXTURE_WRAP_S = 0x2802;
    public const uint GL_TEXTURE_WRAP_T = 0x2803;
    public const uint GL_FRAMEBUFFER = 0x8D40;
    public const uint GL_COLOR_ATTACHMENT0 = 0x8CE0;
    public const uint GL_FRAMEBUFFER_COMPLETE = 0x8CD5;
    public const uint GL_VERTEX_PROGRAM_POINT_SIZE = 0x8642;

    [DllImport("opengl32.dll")] public static extern void glClear(uint mask);
    [DllImport("opengl32.dll")] public static extern void glClearColor(float r, float g, float b, float a);
    [DllImport("opengl32.dll")] public static extern void glViewport(int x, int y, int w, int h);
    [DllImport("opengl32.dll")] public static extern void glEnable(uint cap);
    [DllImport("opengl32.dll")] public static extern void glDisable(uint cap);
    [DllImport("opengl32.dll")] public static extern void glBlendFunc(uint s, uint d);
    [DllImport("opengl32.dll")] public static extern void glDrawArrays(uint mode, int first, int count);
    [DllImport("opengl32.dll")] public static extern void glLineWidth(float w);
    [DllImport("opengl32.dll")] public static extern void glGenTextures(int n, ref uint textures);
    [DllImport("opengl32.dll")] public static extern void glBindTexture(uint target, uint tex);
    [DllImport("opengl32.dll")] public static extern void glTexParameteri(uint target, uint pname, int param);
    [DllImport("opengl32.dll")]
    public static extern void glTexImage2D(uint target, int level, int internalFormat,
                                int width, int height, int border, uint format, uint type, IntPtr pixels);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)] public delegate uint GlCreateShaderD(uint type);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] public delegate void GlShaderSourceD(uint s, int c, IntPtr[] str, IntPtr len);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] public delegate void GlCompileShaderD(uint s);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] public delegate void GlGetShaderivD(uint s, uint p, ref int v);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] public delegate void GlGetShaderInfoLogD(uint s, int max, ref int len, byte[] log);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] public delegate uint GlCreateProgramD();
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] public delegate void GlAttachShaderD(uint p, uint s);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] public delegate void GlLinkProgramD(uint p);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] public delegate void GlGetProgramivD(uint p, uint pn, ref int v);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] public delegate void GlGetProgramInfoLogD(uint p, int max, ref int len, byte[] log);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] public delegate void GlUseProgramD(uint p);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] public delegate void GlDeleteShaderD(uint s);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] public delegate int GlGetUniformLocationD(uint p, byte[] name);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] public delegate void GlUniform1fD(int loc, float v);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] public delegate void GlUniform2fD(int loc, float a, float b);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] public delegate void GlUniform1iD(int loc, int v);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] public delegate void GlActiveTextureD(uint tex);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] public delegate void GlGenD(int n, ref uint id);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] public delegate void GlBindVertexArrayD(uint a);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] public delegate void GlBindBufferLikeD(uint t, uint b);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] public delegate void GlBufferDataD(uint t, IntPtr size, IntPtr data, uint usage);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] public delegate void GlVertexAttribPointerD(uint i, int size, uint type, byte norm, int stride, IntPtr ptr);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] public delegate void GlEnableVertexAttribArrayD(uint i);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] public delegate void GlFramebufferTexture2DD(uint target, uint att, uint textarget, uint tex, int level);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] public delegate uint GlCheckFramebufferStatusD(uint target);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] public delegate int WglSwapIntervalEXTD(int interval);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] public delegate IntPtr WglCreateContextAttribsARB(IntPtr hdc, IntPtr share, int[] attribs);

    public static GlCreateShaderD glCreateShader;
    public static GlShaderSourceD glShaderSource;
    public static GlCompileShaderD glCompileShader;
    public static GlGetShaderivD glGetShaderiv;
    public static GlGetShaderInfoLogD glGetShaderInfoLog;
    public static GlCreateProgramD glCreateProgram;
    public static GlAttachShaderD glAttachShader;
    public static GlLinkProgramD glLinkProgram;
    public static GlGetProgramivD glGetProgramiv;
    public static GlGetProgramInfoLogD glGetProgramInfoLog;
    public static GlUseProgramD glUseProgram;
    public static GlDeleteShaderD glDeleteShader;
    public static GlGetUniformLocationD glGetUniformLocation;
    public static GlUniform1fD glUniform1f;
    public static GlUniform2fD glUniform2f;
    public static GlUniform1iD glUniform1i;
    public static GlActiveTextureD glActiveTexture;
    public static GlGenD glGenVertexArrays;
    public static GlGenD glGenBuffers;
    public static GlGenD glGenFramebuffers;
    public static GlBindVertexArrayD glBindVertexArray;
    public static GlBindBufferLikeD glBindBuffer;
    public static GlBindBufferLikeD glBindFramebuffer;
    public static GlBufferDataD glBufferData;
    public static GlVertexAttribPointerD glVertexAttribPointer;
    public static GlEnableVertexAttribArrayD glEnableVertexAttribArray;
    public static GlFramebufferTexture2DD glFramebufferTexture2D;
    public static GlCheckFramebufferStatusD glCheckFramebufferStatus;
    public static WglSwapIntervalEXTD wglSwapIntervalEXT;

    static T Get<T>(string name) where T : Delegate
    {
        IntPtr p = Win.wglGetProcAddress(name);
        long v = (long)p;
        if (p == IntPtr.Zero || v == 1 || v == 2 || v == 3 || v == -1)
            throw new Exception("Failed to load GL function: " + name);
        return Marshal.GetDelegateForFunctionPointer<T>(p);
    }

    public static void Load()
    {
        glCreateShader = Get<GlCreateShaderD>("glCreateShader");
        glShaderSource = Get<GlShaderSourceD>("glShaderSource");
        glCompileShader = Get<GlCompileShaderD>("glCompileShader");
        glGetShaderiv = Get<GlGetShaderivD>("glGetShaderiv");
        glGetShaderInfoLog = Get<GlGetShaderInfoLogD>("glGetShaderInfoLog");
        glCreateProgram = Get<GlCreateProgramD>("glCreateProgram");
        glAttachShader = Get<GlAttachShaderD>("glAttachShader");
        glLinkProgram = Get<GlLinkProgramD>("glLinkProgram");
        glGetProgramiv = Get<GlGetProgramivD>("glGetProgramiv");
        glGetProgramInfoLog = Get<GlGetProgramInfoLogD>("glGetProgramInfoLog");
        glUseProgram = Get<GlUseProgramD>("glUseProgram");
        glDeleteShader = Get<GlDeleteShaderD>("glDeleteShader");
        glGetUniformLocation = Get<GlGetUniformLocationD>("glGetUniformLocation");
        glUniform1f = Get<GlUniform1fD>("glUniform1f");
        glUniform2f = Get<GlUniform2fD>("glUniform2f");
        glUniform1i = Get<GlUniform1iD>("glUniform1i");
        glActiveTexture = Get<GlActiveTextureD>("glActiveTexture");
        glGenVertexArrays = Get<GlGenD>("glGenVertexArrays");
        glGenBuffers = Get<GlGenD>("glGenBuffers");
        glGenFramebuffers = Get<GlGenD>("glGenFramebuffers");
        glBindVertexArray = Get<GlBindVertexArrayD>("glBindVertexArray");
        glBindBuffer = Get<GlBindBufferLikeD>("glBindBuffer");
        glBindFramebuffer = Get<GlBindBufferLikeD>("glBindFramebuffer");
        glBufferData = Get<GlBufferDataD>("glBufferData");
        glVertexAttribPointer = Get<GlVertexAttribPointerD>("glVertexAttribPointer");
        glEnableVertexAttribArray = Get<GlEnableVertexAttribArrayD>("glEnableVertexAttribArray");
        glFramebufferTexture2D = Get<GlFramebufferTexture2DD>("glFramebufferTexture2D");
        glCheckFramebufferStatus = Get<GlCheckFramebufferStatusD>("glCheckFramebufferStatus");

        IntPtr swap = Win.wglGetProcAddress("wglSwapIntervalEXT");
        if (swap != IntPtr.Zero && (long)swap > 3)
            wglSwapIntervalEXT = Marshal.GetDelegateForFunctionPointer<WglSwapIntervalEXTD>(swap);
    }
}

// =========================================================================
//  Win32 / GDI / WGL P/Invoke
// =========================================================================
internal static class Win
{
    public const uint CS_VREDRAW = 0x0001, CS_HREDRAW = 0x0002, CS_OWNDC = 0x0020;
    public const uint WS_VISIBLE = 0x10000000, WS_OVERLAPPEDWINDOW = 0x00CF0000;
    public const int CW_USEDEFAULT = unchecked((int)0x80000000);
    public const int IDC_ARROW = 32512;
    public const uint PM_REMOVE = 0x0001;
    public const uint WM_DESTROY = 0x0002, WM_SIZE = 0x0005, WM_QUIT = 0x0012;

    public const uint PFD_DRAW_TO_WINDOW = 0x00000004;
    public const uint PFD_SUPPORT_OPENGL = 0x00000020;
    public const uint PFD_DOUBLEBUFFER = 0x00000001;
    public const byte PFD_TYPE_RGBA = 0;
    public const byte PFD_MAIN_PLANE = 0;

    public delegate IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct WNDCLASSEX
    {
        public uint cbSize;
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpszMenuName;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public int pt_x;
        public int pt_y;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int left, top, right, bottom; }

    [StructLayout(LayoutKind.Sequential)]
    public struct PIXELFORMATDESCRIPTOR
    {
        public ushort nSize;
        public ushort nVersion;
        public uint dwFlags;
        public byte iPixelType;
        public byte cColorBits;
        public byte cRedBits, cRedShift, cGreenBits, cGreenShift, cBlueBits, cBlueShift;
        public byte cAlphaBits, cAlphaShift;
        public byte cAccumBits, cAccumRedBits, cAccumGreenBits, cAccumBlueBits, cAccumAlphaBits;
        public byte cDepthBits, cStencilBits, cAuxBuffers;
        public byte iLayerType, bReserved;
        public uint dwLayerMask, dwVisibleMask, dwDamageMask;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr GetModuleHandleW(IntPtr lpModuleName);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern ushort RegisterClassExW(ref WNDCLASSEX wc);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern IntPtr CreateWindowExW(
        uint exStyle, string className, string windowName, uint style,
        int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr hInstance, IntPtr param);

    [DllImport("user32.dll")] public static extern IntPtr DefWindowProcW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] public static extern void PostQuitMessage(int code);
    [DllImport("user32.dll")] public static extern IntPtr LoadCursorW(IntPtr hInstance, IntPtr lpCursorName);
    [DllImport("user32.dll")] public static extern IntPtr GetDC(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern int ReleaseDC(IntPtr hWnd, IntPtr hdc);
    [DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr hWnd, out RECT rc);

    [DllImport("user32.dll")] public static extern bool PeekMessageW(out MSG msg, IntPtr hWnd, uint min, uint max, uint remove);
    [DllImport("user32.dll")] public static extern bool TranslateMessage(ref MSG msg);
    [DllImport("user32.dll")] public static extern IntPtr DispatchMessageW(ref MSG msg);

    [DllImport("gdi32.dll")] public static extern int ChoosePixelFormat(IntPtr hdc, ref PIXELFORMATDESCRIPTOR pfd);
    [DllImport("gdi32.dll")] public static extern bool SetPixelFormat(IntPtr hdc, int fmt, ref PIXELFORMATDESCRIPTOR pfd);
    [DllImport("gdi32.dll")] public static extern bool SwapBuffers(IntPtr hdc);

    [DllImport("opengl32.dll")] public static extern IntPtr wglCreateContext(IntPtr hdc);
    [DllImport("opengl32.dll")] public static extern bool wglMakeCurrent(IntPtr hdc, IntPtr ctx);
    [DllImport("opengl32.dll")] public static extern bool wglDeleteContext(IntPtr ctx);
    [DllImport("opengl32.dll", CharSet = CharSet.Ansi)]
    public static extern IntPtr wglGetProcAddress(string name);
}
