using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace ChromaPanel
{
    /// <summary>
    /// HLSL for the desktop filters. Every filter here works at native
    /// resolution: the desktop is already full size, so there is nothing to
    /// upscale - only detail to restore.
    ///
    ///  - CAS and RCAS follow AMD FidelityFX CAS (ffx_cas.h) and FSR 1 RCAS
    ///    (ffx_fsr1.h), MIT. Re-implemented, not copied.
    ///  - AdaptiveSharpen is bacondither's shader, BSD 2-clause, ported from
    ///    ReShade FX to plain HLSL with its notice kept in place.
    ///  - Luma sharpen is ChromaPanel's own.
    ///  - Anime4K lives in Anime4K.cs, generated from bloc97's MIT GLSL.
    /// See THIRD_PARTY_NOTICES.md. tests/GpuTests.cs checks each of them.
    /// </summary>
    internal static class SharpenShader
    {
        /// <summary>Shared by every filter except Anime4K, which declares its own textures.</summary>
        public const string Common = @"
Texture2D<float4> Src : register(t0);
Texture2D<float4> Aux : register(t1);

cbuffer Params : register(b0)
{
    float Strength;   // 0 = pass-through, 1 = strongest
    uint  Mode;
    float2 Unused;
};

float4 VS(uint id : SV_VertexID) : SV_Position
{
    // One triangle that covers the whole target: (-1,1) (3,1) (-1,-3).
    float2 t = float2((id << 1) & 2, id & 2);
    return float4(t.x * 2.0 - 1.0, 1.0 - t.y * 2.0, 0.0, 1.0);
}

float3 Px(int2 p, int2 hi) { return Src.Load(int3(clamp(p, int2(0, 0), hi), 0)).rgb; }

int2 SrcHi() { uint w, h; Src.GetDimensions(w, h); return int2(w, h) - 1; }
";

        // ------------------------------------------------------------------
        public const string Basic = @"
static const float RCAS_LIMIT = 0.25 - 1.0 / 16.0;

float4 PS_Cas(float4 pos : SV_Position) : SV_Target
{
    int2 hi = SrcHi(), p = int2(pos.xy);
    float3 a = Px(p + int2(-1, -1), hi), b = Px(p + int2(0, -1), hi), c = Px(p + int2(1, -1), hi);
    float3 d = Px(p + int2(-1,  0), hi), e = Px(p,                hi), f = Px(p + int2(1,  0), hi);
    float3 g = Px(p + int2(-1,  1), hi), h = Px(p + int2(0,  1), hi), i = Px(p + int2(1,  1), hi);

    // Soft min/max: the cross plus the full 3x3 (AMD's CAS_BETTER_DIAGONALS),
    // which makes both twice as large - hence the 2.0 below.
    float3 mn = min(min(min(d, e), min(f, b)), h);
    float3 mx = max(max(max(d, e), max(f, b)), h);
    mn += min(min(min(mn, a), min(c, g)), i);
    mx += max(max(max(mx, a), max(c, g)), i);

    // Sharpen less where the signal is already close to black or white.
    float3 amp = sqrt(saturate(min(mn, 2.0 - mx) / max(mx, 1e-5)));

    // AMD maps sharpness 0..1 to a peak of -1/8..-1/5, so its 0 still
    // sharpens. Here 0 is off, giving the slider a true zero; 1 is AMD's max.
    float3 w = amp * (-0.2 * Strength);
    return float4(saturate((b * w + d * w + f * w + h * w + e) / (1.0 + 4.0 * w)), 1.0);
}

float4 PS_Rcas(float4 pos : SV_Position) : SV_Target
{
    int2 hi = SrcHi(), p = int2(pos.xy);
    float3 b = Px(p + int2(0, -1), hi);
    float3 d = Px(p + int2(-1, 0), hi), e = Px(p, hi), f = Px(p + int2(1, 0), hi);
    float3 h = Px(p + int2(0,  1), hi);

    float3 mn4 = min(min(b, d), min(f, h));
    float3 mx4 = max(max(b, d), max(f, h));

    // The strongest negative lobe that cannot push the result outside the
    // range of its four neighbours - RCAS's 'robust' limiter.
    float3 hitMin = min(mn4, e) / max(4.0 * mx4, 1e-5);
    float3 hitMax = (1.0 - max(mx4, e)) / min(4.0 * mn4 - 4.0, -1e-5);
    float3 l = max(-hitMin, hitMax);
    float lobe = max(-RCAS_LIMIT, min(max(l.r, max(l.g, l.b)), 0.0)) * Strength;

    return float4(saturate((lobe * (b + d + f + h) + e) / (4.0 * lobe + 1.0)), 1.0);
}

// Luma sharpen (ChromaPanel's own). An unsharp mask on brightness only: the
// same correction is added to R, G and B, so it cannot widen the coloured
// fringes ClearType and OLED sub-pixel layouts put on text. The clamp keeps
// halos from forming around hard edges.
static const float3 LUMA = float3(0.2126, 0.7152, 0.0722);
static const float LUMA_CLAMP = 0.05;

float4 PS_Luma(float4 pos : SV_Position) : SV_Target
{
    int2 hi = SrcHi(), p = int2(pos.xy);
    float3 c = Px(p, hi);
    float l = dot(c, LUMA);
    float blur = 4.0 * l
        + 2.0 * (dot(Px(p + int2(0, -1), hi), LUMA) + dot(Px(p + int2(-1, 0), hi), LUMA)
               + dot(Px(p + int2(1,  0), hi), LUMA) + dot(Px(p + int2(0,  1), hi), LUMA))
        +       (dot(Px(p + int2(-1, -1), hi), LUMA) + dot(Px(p + int2(1, -1), hi), LUMA)
               + dot(Px(p + int2(-1,  1), hi), LUMA) + dot(Px(p + int2(1,  1), hi), LUMA));
    blur /= 16.0;
    float detail = clamp((l - blur) * (Strength * 3.0), -LUMA_CLAMP, LUMA_CLAMP);
    return float4(saturate(c + detail), 1.0);
}
";

        // ------------------------------------------------------------------
        public const string Adaptive = @"
// Copyright (c) 2015-2018, bacondither
// All rights reserved.
//
// Redistribution and use in source and binary forms, with or without
// modification, are permitted provided that the following conditions
// are met:
// 1. Redistributions of source code must retain the above copyright
//    notice, this list of conditions and the following disclaimer
//    in this position and unchanged.
// 2. Redistributions in binary form must reproduce the above copyright
//    notice, this list of conditions and the following disclaimer in the
//    documentation and/or other materials provided with the distribution.
//
// THIS SOFTWARE IS PROVIDED BY THE AUTHORS ``AS IS'' AND ANY EXPRESS OR
// IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED WARRANTIES
// OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE DISCLAIMED.
// IN NO EVENT SHALL THE AUTHOR BE LIABLE FOR ANY DIRECT, INDIRECT,
// INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES (INCLUDING, BUT
// NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES; LOSS OF USE,
// DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND ON ANY
// THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
// (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF
// THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.

// Adaptive sharpen - version 2018-04-14
// EXPECTS FULL RANGE GAMMA LIGHT
//
// Ported from the ReShade FX version (fast_ops = 1) to plain HLSL for
// ChromaPanel: ReShade's sampler macros became integer Loads with edge
// clamping, rcp() became a division, and curve_height is driven by the
// strength slider (50% = the shader's default of 1.0). The other tunables
// keep their defaults. Pass 1 writes (edge, luma) to an RG16F target.

static const float curveslope   = 0.5;
static const float L_overshoot  = 0.003;
static const float L_compr_low  = 0.167;
static const float L_compr_high = 0.334;
static const float D_overshoot  = 0.009;
static const float D_compr_low  = 0.250;
static const float D_compr_high = 0.500;
static const float scale_lim    = 0.1;
static const float scale_cs     = 0.056;
static const float pm_p         = 0.7;

#define sqr(a)         ( (a)*(a) )
#define max4(a,b,c,d)  ( max(max(a, b), max(c, d)) )
#define getB(x,y)      ( saturate(Px(p + int2(x, y), hi)) )
#define getT(x,y)      ( Aux.Load(int3(clamp(p + int2(x, y), int2(0, 0), hi), 0)).xy )
#define soft_if(a,b,c) ( saturate((a + b + c + 0.056)/(abs(maxedge) + 0.03) - 0.85) )
#define soft_lim(v,s)  ( saturate(abs(v/s)*(27 + sqr(v/s))/(27 + 9*sqr(v/s)))*s )
#define wpmean(a,b,w)  ( pow(abs(w)*pow(abs(a), pm_p) + abs(1-w)*pow(abs(b), pm_p), (1.0/pm_p)) )
#define b_diff(pix)    ( abs(blur - c[pix]) )
#define min_overshoot  ( min(abs(L_overshoot), abs(D_overshoot)) )
#define fskip_th       ( 0.114*pow(min_overshoot, 0.676) + 3.20e-4 )
#define mdiff(a,b,c,d,e,f,g) ( abs(luma[g] - luma[a]) + abs(luma[g] - luma[b])       \
                             + abs(luma[g] - luma[c]) + abs(luma[g] - luma[d])       \
                             + 0.5*(abs(luma[g] - luma[e]) + abs(luma[g] - luma[f])) )

float4 PS_AS0(float4 pos : SV_Position) : SV_Target
{
    int2 hi = SrcHi(), p = int2(pos.xy);

    // [                c9                ]
    // [           c1,  c2,  c3           ]
    // [      c10, c4,  c0,  c5, c11      ]
    // [           c6,  c7,  c8           ]
    // [                c12               ]
    float3 c[13] = { getB( 0, 0), getB(-1,-1), getB( 0,-1), getB( 1,-1), getB(-1, 0),
                     getB( 1, 0), getB(-1, 1), getB( 0, 1), getB( 1, 1), getB( 0,-2),
                     getB(-2, 0), getB( 2, 0), getB( 0, 2) };

    // Colour to luma, fast approx gamma, avg of rec. 709 & 601 luma coeffs
    float luma = sqrt(dot(float3(0.2558, 0.6511, 0.0931), sqr(c[0])));

    // Blur, gauss 3x3
    float3 blur = (2*(c[2]+c[4]+c[5]+c[7]) + (c[1]+c[3]+c[6]+c[8]) + 4*c[0])/16;

    // Contrast compression, center = 0.5, scaled to 1/3
    float c_comp = saturate(4.0/15.0 + 0.9*exp2(dot(blur, -37.0/15.0)));

    // Edge detection, relative matrix weights
    // [          1          ]
    // [      4,  5,  4      ]
    // [  1,  5,  6,  5,  1  ]
    // [      4,  5,  4      ]
    // [          1          ]
    float edge = length( 1.38*(b_diff(0))
                       + 1.15*(b_diff(2) + b_diff(4)  + b_diff(5)  + b_diff(7))
                       + 0.92*(b_diff(1) + b_diff(3)  + b_diff(6)  + b_diff(8))
                       + 0.23*(b_diff(9) + b_diff(10) + b_diff(11) + b_diff(12)) );

    return float4(edge*c_comp, luma, 0.0, 0.0);
}

float4 PS_AS1(float4 pos : SV_Position) : SV_Target
{
    int2 hi = SrcHi(), p = int2(pos.xy);
    float curve_height = Strength * 2.0;
    float3 origsat = getB(0, 0);

    // [                d22               ]
    // [           d24, d9,  d23          ]
    // [      d21, d1,  d2,  d3, d18      ]
    // [ d19, d10, d4,  d0,  d5, d11, d16 ]
    // [      d20, d6,  d7,  d8, d17      ]
    // [           d15, d12, d14          ]
    // [                d13               ]
    float2 d[25] = { getT( 0, 0), getT(-1,-1), getT( 0,-1), getT( 1,-1), getT(-1, 0),
                     getT( 1, 0), getT(-1, 1), getT( 0, 1), getT( 1, 1), getT( 0,-2),
                     getT(-2, 0), getT( 2, 0), getT( 0, 2), getT( 0, 3), getT( 1, 2),
                     getT(-1, 2), getT( 3, 0), getT( 2, 1), getT( 2,-1), getT(-3, 0),
                     getT(-2, 1), getT(-2,-1), getT( 0,-3), getT( 1,-2), getT(-1,-2) };

    // Allow for higher overshoot if the current edge pixel is surrounded by similar edge pixels
    float maxedge = max4( max4(d[1].x,d[2].x,d[3].x,d[4].x), max4(d[5].x,d[6].x,d[7].x,d[8].x),
                          max4(d[9].x,d[10].x,d[11].x,d[12].x), d[0].x );

    float sbe = soft_if(d[2].x,d[9].x, d[22].x)*soft_if(d[7].x,d[12].x,d[13].x)  // x dir
              + soft_if(d[4].x,d[10].x,d[19].x)*soft_if(d[5].x,d[11].x,d[16].x)  // y dir
              + soft_if(d[1].x,d[24].x,d[21].x)*soft_if(d[8].x,d[14].x,d[17].x)  // z dir
              + soft_if(d[3].x,d[23].x,d[18].x)*soft_if(d[6].x,d[20].x,d[15].x); // w dir

    float2 cs = lerp( float2(L_compr_low,  D_compr_low),
                      float2(L_compr_high, D_compr_high), saturate(1.091*sbe - 2.282) );

    float luma[25] = { d[0].y,  d[1].y,  d[2].y,  d[3].y,  d[4].y,
                       d[5].y,  d[6].y,  d[7].y,  d[8].y,  d[9].y,
                       d[10].y, d[11].y, d[12].y, d[13].y, d[14].y,
                       d[15].y, d[16].y, d[17].y, d[18].y, d[19].y,
                       d[20].y, d[21].y, d[22].y, d[23].y, d[24].y };

    // Pre-calculated default squared kernel weights
    const float3 W1 = float3(0.5,           1.0, 1.41421356237); // 0.25, 1.0, 2.0
    const float3 W2 = float3(0.86602540378, 1.0, 0.54772255751); // 0.75, 1.0, 0.3

    // Transition to a concave kernel if the center edge val is above thr
    float3 dW = sqr(lerp( W1, W2, saturate(2.4*d[0].x - 0.82) ));

    float mdiff_c0 = 0.02 + 3*( abs(luma[0]-luma[2]) + abs(luma[0]-luma[4])
                              + abs(luma[0]-luma[5]) + abs(luma[0]-luma[7])
                              + 0.25*(abs(luma[0]-luma[1]) + abs(luma[0]-luma[3])
                                     +abs(luma[0]-luma[6]) + abs(luma[0]-luma[8])) );

    // Use lower weights for pixels in a more active area relative to center pixel area
    // This results in narrower and less visible overshoots around sharp edges
    float weights[12] = { ( min(mdiff_c0/mdiff(24, 21, 2,  4,  9,  10, 1),  dW.y) ),   // c1
                          ( dW.x ),                                                    // c2
                          ( min(mdiff_c0/mdiff(23, 18, 5,  2,  9,  11, 3),  dW.y) ),   // c3
                          ( dW.x ),                                                    // c4
                          ( dW.x ),                                                    // c5
                          ( min(mdiff_c0/mdiff(4,  20, 15, 7,  10, 12, 6),  dW.y) ),   // c6
                          ( dW.x ),                                                    // c7
                          ( min(mdiff_c0/mdiff(5,  7,  17, 14, 12, 11, 8),  dW.y) ),   // c8
                          ( min(mdiff_c0/mdiff(2,  24, 23, 22, 1,  3,  9),  dW.z) ),   // c9
                          ( min(mdiff_c0/mdiff(20, 19, 21, 4,  1,  6,  10), dW.z) ),   // c10
                          ( min(mdiff_c0/mdiff(17, 5,  18, 16, 3,  8,  11), dW.z) ),   // c11
                          ( min(mdiff_c0/mdiff(13, 15, 7,  14, 6,  8,  12), dW.z) ) }; // c12

    weights[0] = (max(max((weights[8]  + weights[9])/4,  weights[0]), 0.25) + weights[0])/2;
    weights[2] = (max(max((weights[8]  + weights[10])/4, weights[2]), 0.25) + weights[2])/2;
    weights[5] = (max(max((weights[9]  + weights[11])/4, weights[5]), 0.25) + weights[5])/2;
    weights[7] = (max(max((weights[10] + weights[11])/4, weights[7]), 0.25) + weights[7])/2;

    // Calculate the negative part of the laplace kernel and the low threshold weight
    float lowthrsum   = 0;
    float weightsum   = 0;
    float neg_laplace = 0;

    [unroll] for (int pix = 0; pix < 12; ++pix)
    {
        float lowthr = clamp((13.2*d[pix + 1].x - 0.221), 0.01, 1);
        neg_laplace += sqr(luma[pix + 1])*(weights[pix]*lowthr);
        weightsum += weights[pix]*lowthr;
        lowthrsum += lowthr/12;
    }

    neg_laplace = sqrt(neg_laplace/weightsum);

    // Compute sharpening magnitude function
    float sharpen_val = curve_height/(curve_height*curveslope*pow(abs(d[0].x), 3.5) + 0.625);

    // Calculate sharpening diff and scale
    float sharpdiff = (d[0].y - neg_laplace)*(lowthrsum*sharpen_val + 0.01);

    // Skip limiting on flat areas where sharpdiff is low
    [branch] if (abs(sharpdiff) > fskip_th)
    {
        // Calculate local near min & max, partial sort
        {
            float temp; int i; int ii;

            // 1st iteration
            [unroll] for (i = 0; i < 24; i += 2)
            {
                temp = luma[i];
                luma[i]   = min(luma[i], luma[i+1]);
                luma[i+1] = max(temp, luma[i+1]);
            }
            [unroll] for (ii = 24; ii > 0; ii -= 2)
            {
                temp = luma[0];
                luma[0]    = min(luma[0], luma[ii]);
                luma[ii]   = max(temp, luma[ii]);

                temp = luma[24];
                luma[24]   = max(luma[24], luma[ii-1]);
                luma[ii-1] = min(temp, luma[ii-1]);
            }

            // 2nd iteration
            [unroll] for (i = 1; i < 23; i += 2)
            {
                temp = luma[i];
                luma[i]   = min(luma[i], luma[i+1]);
                luma[i+1] = max(temp, luma[i+1]);
            }
            [unroll] for (ii = 23; ii > 1; ii -= 2)
            {
                temp = luma[1];
                luma[1]    = min(luma[1], luma[ii]);
                luma[ii]   = max(temp, luma[ii]);

                temp = luma[23];
                luma[23]   = max(luma[23], luma[ii-1]);
                luma[ii-1] = min(temp, luma[ii-1]);
            }
        }

        // Calculate tanh scale factors
        float nmax = (max(luma[23], d[0].y)*2 + luma[24])/3;
        float nmin = (min(luma[1],  d[0].y)*2 + luma[0])/3;

        float min_dist  = min(abs(nmax - d[0].y), abs(d[0].y - nmin));
        float pos_scale = min_dist + L_overshoot;
        float neg_scale = min_dist + D_overshoot;

        pos_scale = min(pos_scale, scale_lim*(1 - scale_cs) + pos_scale*scale_cs);
        neg_scale = min(neg_scale, scale_lim*(1 - scale_cs) + neg_scale*scale_cs);

        // Soft limited anti-ringing with tanh, wpmean to control compression slope
        sharpdiff = wpmean( max(sharpdiff, 0), soft_lim( max(sharpdiff, 0), pos_scale ), cs.x )
                  - wpmean( min(sharpdiff, 0), soft_lim( min(sharpdiff, 0), neg_scale ), cs.y );
    }

    // Compensate for saturation loss/gain while making pixels brighter/darker
    float sharpdiff_lim = saturate(d[0].y + sharpdiff) - d[0].y;
    float satmul = (d[0].y + max(sharpdiff_lim*0.9, sharpdiff_lim)*1.03 + 0.03)/(d[0].y + 0.03);
    float3 res = d[0].y + (sharpdiff_lim*3 + sharpdiff)/4 + (origsat - d[0].y)*satmul;

    return float4(saturate(res), 1.0);
}
";
    }

    // ======================================================================
    /// <summary>
    /// Owns the shaders and scratch textures for one device and draws one
    /// filtered frame. Shaders compile the first time a filter is used;
    /// scratch textures persist between frames and follow the frame size.
    /// </summary>
    internal sealed class Sharpener : IDisposable
    {
        const uint FORMAT_R16G16_FLOAT = 34, FORMAT_R16G16B16A16_FLOAT = 10;
        const int MaxInputs = 8;

        readonly IntPtr _dev;
        readonly Dx.Context _ctx;
        IntPtr _vs, _cb;
        IntPtr _params = Marshal.AllocHGlobal(16);
        float _strength = float.NaN;
        uint _mode = uint.MaxValue;
        readonly Dictionary<string, IntPtr> _ps = new Dictionary<string, IntPtr>();
        readonly Dictionary<string, Scratch> _scratch = new Dictionary<string, Scratch>();
        readonly IntPtr[] _views = new IntPtr[MaxInputs];
        readonly IntPtr[] _none = new IntPtr[MaxInputs];

        sealed class Scratch { public IntPtr Tex, Rtv, Srv; public int W, H; }

        public Sharpener(IntPtr device, Dx.Context ctx)
        {
            _dev = device;
            _ctx = ctx;
            _vs = Dx.CompileVS(device, SharpenShader.Common, "VS");
            _cb = Dx.CreateConstantBuffer(device, 16);
        }

        /// <param name="srv">The source image.</param>
        /// <param name="rtv">Target, the same size as the source.</param>
        public void Render(IntPtr srv, IntPtr rtv, int width, int height, SharpenFilter filter, float strength)
        {
            IntPtr c = _ctx.Ptr;
            uint mode = (uint)filter;
            if (strength != _strength || mode != _mode)
            {
                byte[] b = new byte[16];
                Buffer.BlockCopy(BitConverter.GetBytes(strength), 0, b, 0, 4);
                Buffer.BlockCopy(BitConverter.GetBytes(mode), 0, b, 4, 4);
                Marshal.Copy(b, 0, _params, 16);
                _ctx.UpdateSubresource(c, _cb, 0, IntPtr.Zero, _params, 0, 0);
                _strength = strength;
                _mode = mode;
            }

            _ctx.IASetInputLayout(c, IntPtr.Zero);
            _ctx.IASetPrimitiveTopology(c, Dx.TOPOLOGY_TRIANGLELIST);
            _ctx.VSSetShader(c, _vs, IntPtr.Zero, 0);
            IntPtr cb = _cb;
            _ctx.PSSetConstantBuffers(c, 0, 1, ref cb);
            var vp = new Dx.VIEWPORT { Width = width, Height = height, MaxDepth = 1f };
            _ctx.RSSetViewports(c, 1, ref vp);

            switch (filter)
            {
                case SharpenFilter.Rcas: Pass(Basic("PS_Rcas"), rtv, srv); break;
                case SharpenFilter.Luma: Pass(Basic("PS_Luma"), rtv, srv); break;
                case SharpenFilter.Adaptive:
                    {
                        Scratch edge = Tmp("as_edge", width, height, FORMAT_R16G16_FLOAT);
                        Pass(Shader("PS_AS0", SharpenShader.Common + SharpenShader.Adaptive), edge.Rtv, srv);
                        Pass(Shader("PS_AS1", SharpenShader.Common + SharpenShader.Adaptive), rtv, srv, edge.Srv);
                        break;
                    }
                case SharpenFilter.Anime4K:
                    for (int i = 0; i < Anime4K.Entries.Length; i++)
                    {
                        string[] ins = Anime4K.Inputs[i];
                        var views = new IntPtr[ins.Length];
                        for (int k = 0; k < ins.Length; k++)
                            views[k] = Tmp(ins[k], width, height, FORMAT_R16G16B16A16_FLOAT).Srv;
                        IntPtr target = Anime4K.Outputs[i] == "MAIN"
                            ? rtv
                            : Tmp(Anime4K.Outputs[i], width, height, FORMAT_R16G16B16A16_FLOAT).Rtv;
                        // The desktop stays on t0 for every pass: each one reads its size from it.
                        Pass(Shader(Anime4K.Entries[i], Anime4K.Hlsl), target, srv, views);
                    }
                    break;
                default: Pass(Basic("PS_Cas"), rtv, srv); break;
            }
        }

        IntPtr Basic(string entry) { return Shader(entry, SharpenShader.Common + SharpenShader.Basic); }

        IntPtr Shader(string entry, string source)
        {
            IntPtr ps;
            if (!_ps.TryGetValue(entry, out ps))
            {
                ps = Dx.CompilePS(_dev, source, entry);
                _ps[entry] = ps;
            }
            return ps;
        }

        Scratch Tmp(string name, int w, int h, uint format)
        {
            Scratch s;
            if (_scratch.TryGetValue(name, out s) && s.W == w && s.H == h) return s;
            if (s != null) Free(s);
            var d = new Dx.TEXTURE2D_DESC
            {
                Width = (uint)w, Height = (uint)h, MipLevels = 1, ArraySize = 1, Format = format, SampleCount = 1,
                Usage = Dx.USAGE_DEFAULT, BindFlags = Dx.BIND_SHADER_RESOURCE | Dx.BIND_RENDER_TARGET
            };
            s = new Scratch { W = w, H = h };
            s.Tex = Dx.CreateTexture2D(_dev, ref d, IntPtr.Zero);
            s.Rtv = Dx.CreateRTV(_dev, s.Tex);
            s.Srv = Dx.CreateSRV(_dev, s.Tex);
            _scratch[name] = s;
            return s;
        }

        /// <summary>One full-screen draw: t0 = source, t1.. = extra inputs.</summary>
        void Pass(IntPtr ps, IntPtr target, IntPtr t0, params IntPtr[] more)
        {
            IntPtr c = _ctx.Ptr;
            IntPtr rt = target;
            _ctx.OMSetRenderTargets(c, 1, ref rt, IntPtr.Zero);
            _ctx.PSSetShader(c, ps, IntPtr.Zero, 0);

            int n = 1 + more.Length;
            _views[0] = t0;
            for (int i = 0; i < more.Length; i++) _views[i + 1] = more[i];
            _ctx.PSSetShaderResourcesArray(c, 0, (uint)n, _views);
            _ctx.Draw(c, 3, 0);

            // Unbind, so the next pass can write a texture this one read.
            _ctx.PSSetShaderResourcesArray(c, 0, (uint)n, _none);
            IntPtr nothing = IntPtr.Zero;
            _ctx.OMSetRenderTargets(c, 0, ref nothing, IntPtr.Zero);
        }

        static void Free(Scratch s)
        {
            Dx.Release(ref s.Srv);
            Dx.Release(ref s.Rtv);
            Dx.Release(ref s.Tex);
        }

        public void Dispose()
        {
            foreach (Scratch s in _scratch.Values) Free(s);
            _scratch.Clear();
            var keys = new List<string>(_ps.Keys);
            foreach (string k in keys) { IntPtr p = _ps[k]; Dx.Release(ref p); }
            _ps.Clear();
            Dx.Release(ref _cb);
            Dx.Release(ref _vs);
            if (_params != IntPtr.Zero) { Marshal.FreeHGlobal(_params); _params = IntPtr.Zero; }
        }
    }
}
