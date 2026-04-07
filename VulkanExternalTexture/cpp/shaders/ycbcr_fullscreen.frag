#version 450

layout(set = 0, binding = 0) uniform sampler2D u_source;

layout(location = 0) in vec2 v_uv;
layout(location = 0) out vec4 o_color;

layout(push_constant) uniform Push
{
    ivec4 p;
    vec4 mul;
    vec4 add;
} pc;

vec3 yuv_to_rgb(vec3 yuv, int matrix, int input_mode, int flags)
{
    int swap_uv = (flags & 1);
    int invert_u = (flags & 2);
    int invert_v = (flags & 4);
    int order = (flags >> 3) & 7;

    float c0 = yuv.r;
    float c1 = yuv.g;
    float c2 = yuv.b;

    float y = c0;
    float u = c1;
    float v = c2;
    // order: 0=YUV,1=YVU,2=UYV,3=UVY,4=VYU,5=VUY
    if (order == 1) { y = c0; u = c2; v = c1; }
    else if (order == 2) { y = c1; u = c0; v = c2; }
    else if (order == 3) { y = c2; u = c0; v = c1; }
    else if (order == 4) { y = c1; u = c2; v = c0; }
    else if (order == 5) { y = c2; u = c1; v = c0; }

    if (swap_uv != 0)
    {
        float t = u;
        u = v;
        v = t;
    }
    if (invert_u != 0) { u = 1.0 - u; }
    if (invert_v != 0) { v = 1.0 - v; }

    // input_mode:
    // 0 = normalized Y,U,V in [0..1] (U/V neutral at 0.5, Y already range-expanded)
    // 1 = byte-narrow (Java-style): treat inputs as [0..255] scaled into [0..1], apply 16/128 offsets + 1.164
    // 2 = byte-full: treat inputs as [0..255] scaled into [0..1], U/V offset by 128 only
    float y_scaled = y;
    float u_scaled = u;
    float v_scaled = v;

    if (input_mode == 1)
    {
        // Java narrow-range conversion (BT.601 coefficients, with 1.164 luma scale).
        float y1 = (y * 255.0 - 16.0) * 1.164;
        float u1 = (u * 255.0 - 128.0);
        float v1 = (v * 255.0 - 128.0);
        y_scaled = y1 / 255.0;
        u_scaled = u1 / 255.0;
        v_scaled = v1 / 255.0;
    }
    else if (input_mode == 2)
    {
        // Full range bytes: neutral chroma at 128.
        u_scaled = u_scaled - (128.0 / 255.0);
        v_scaled = v_scaled - (128.0 / 255.0);
    }
    else
    {
        // Normalized conversion: Y already expanded, chroma neutral at 0.5.
        u_scaled = u_scaled - 0.5;
        v_scaled = v_scaled - 0.5;
    }

    y_scaled = clamp(y_scaled, 0.0, 1.0);

    vec3 rgb;
    if (matrix == 1)
    {
        // BT.709
        rgb.r = y_scaled + 1.5748 * v_scaled;
        rgb.g = y_scaled - 0.1873 * u_scaled - 0.4681 * v_scaled;
        rgb.b = y_scaled + 1.8556 * u_scaled;
    }
    else
    {
        // BT.601
        // Note: input_mode==1 uses Java-style coefficients below for closer match.
        if (input_mode == 1)
        {
            rgb.r = y_scaled + 1.5960 * v_scaled;
            rgb.g = y_scaled - 0.3910 * u_scaled - 0.8130 * v_scaled;
            rgb.b = y_scaled + 2.0180 * u_scaled;
        }
        else
        {
            rgb.r = y_scaled + 1.4020 * v_scaled;
            rgb.g = y_scaled - 0.3441 * u_scaled - 0.7141 * v_scaled;
            rgb.b = y_scaled + 1.7720 * u_scaled;
        }
    }

    return clamp(rgb, 0.0, 1.0);
}

void main()
{
    // When the sampler uses VK_SAMPLER_YCBCR_MODEL_CONVERSION_YCBCR_IDENTITY, the sample returns (Y, Cb, Cr).
    vec3 sampleRgbOrYuv = texture(u_source, v_uv).rgb;

    // pc.p.x: debug mode (0=normal, 1=show Y, 2=show U, 3=show V)
    // pc.p.y: matrix (0=BT.601, 1=BT.709)
    // pc.p.z: input_mode (0=normalized, 1=byte-narrow (Java), 2=byte-full)
    // pc.p.w: flags bitmask
    //   bit0: swapUV
    //   bit1: invertU
    //   bit2: invertV
    //   bits3-5: channelOrder (0..5)
    int debugMode = pc.p.x;
    int inputMode = pc.p.z;
    if (debugMode == 1)
    {
        o_color = vec4(sampleRgbOrYuv.rrr, 1.0);
        return;
    }
    if (debugMode == 2)
    {
        o_color = vec4(sampleRgbOrYuv.ggg, 1.0);
        return;
    }
    if (debugMode == 3)
    {
        o_color = vec4(sampleRgbOrYuv.bbb, 1.0);
        return;
    }

    if (inputMode < 0)
    {
        // Hardware YCbCr conversion already returned RGB; output directly.
        vec4 outColor = vec4(sampleRgbOrYuv, 1.0);
        outColor = clamp(outColor * pc.mul + pc.add, 0.0, 1.0);
        o_color = outColor;
        return;
    }

    vec3 rgb = yuv_to_rgb(sampleRgbOrYuv, pc.p.y, inputMode, pc.p.w);
    vec4 outColor = vec4(rgb, 1.0);
    outColor = clamp(outColor * pc.mul + pc.add, 0.0, 1.0);
    o_color = outColor;
}
