using System.Globalization;
using System.Text;

namespace MonitorToneFix.Core.Recolor;

/// <summary>
/// Renders <see cref="RecolorParameters"/> into a picom custom window shader
/// (GLSL, `vec4 window_shader()` entry point — see `man picom`, SHADER INTERFACE).
/// Values are baked in as literals: picom shaders take no external uniforms besides
/// the fixed set picom itself provides, so every slider change means regenerating
/// this file and asking picom to reload (SIGUSR1).
/// </summary>
public static class PicomShaderGenerator
{
    public static string Generate(RecolorParameters p)
    {
        var (ar, ag, ab) = p.BandA.TargetColor.ToUnitFloat();
        var (br, bg, bb) = p.BandB.TargetColor.ToUnitFloat();

        float thresholdA = p.BandA.LowerLuminance / 255f;
        float featherA = p.BandA.FeatherPercent / 100f * 0.2f;
        float neutralityToleranceA = 1f - (p.BandA.NeutralityTolerance / 255f);
        float strengthA = p.BandA.IntensityPercent / 100f;

        float thresholdB = p.BandB.LowerLuminance / 255f;
        float featherB = p.BandB.FeatherPercent / 100f * 0.2f;
        float redSensitivityB = p.BandB.RedSensitivity / 255f;
        float strengthB = p.BandB.IntensityPercent / 100f;

        float maxBrightness = p.Global.MaxHighlightBrightness / 255f;
        float luminancePreservation = p.Global.LuminancePreservationPercent / 100f;

        var sb = new StringBuilder();
        var inv = CultureInfo.InvariantCulture;
        string F(float v) => v.ToString("0.######", inv);

        sb.AppendLine("#version 330");
        sb.AppendLine("in vec2 texcoord;");
        sb.AppendLine("uniform sampler2D tex;");
        sb.AppendLine("vec4 default_post_processing(vec4 c);");
        sb.AppendLine();
        sb.AppendLine("// MonitorToneFix — gerado automaticamente. Nao edite a mao: sera sobrescrito");
        sb.AppendLine("// a cada ajuste de controle deslizavel.");
        sb.AppendLine();
        sb.AppendLine($"const float thresholdA = {F(thresholdA)};");
        sb.AppendLine($"const float featherA = {F(featherA)};");
        sb.AppendLine($"const float neutralityMinimumA = {F(neutralityToleranceA)};");
        sb.AppendLine($"const float strengthA = {F(strengthA)};");
        sb.AppendLine($"const vec3 targetColorA = vec3({F(ar)}, {F(ag)}, {F(ab)});");
        sb.AppendLine();
        sb.AppendLine($"const float thresholdB = {F(thresholdB)};");
        sb.AppendLine($"const float featherB = {F(featherB)};");
        sb.AppendLine($"const float redSensitivityB = {F(redSensitivityB)};");
        sb.AppendLine($"const float strengthB = {F(strengthB)};");
        sb.AppendLine($"const vec3 targetColorB = vec3({F(br)}, {F(bg)}, {F(bb)});");
        sb.AppendLine();
        sb.AppendLine($"const float maxBrightness = {F(maxBrightness)};");
        sb.AppendLine($"const float luminancePreservation = {F(luminancePreservation)};");
        sb.AppendLine();
        sb.AppendLine("""
            vec4 window_shader() {
                vec4 c = texelFetch(tex, ivec2(texcoord), 0);
                vec3 rgb = c.rgb;

                float luma = dot(rgb, vec3(0.2126, 0.7152, 0.0722));

                // Faixa A: brancos neutros (canais altos e proximos entre si).
                float maxChannel = max(rgb.r, max(rgb.g, rgb.b));
                float minChannel = min(rgb.r, min(rgb.g, rgb.b));
                float spread = maxChannel - minChannel;
                float neutrality = 1.0 - clamp(spread / max(maxChannel, 0.001), 0.0, 1.0);
                float lightMaskA = smoothstep(thresholdA - featherA, thresholdA + featherA, luma);
                float neutralMaskA = smoothstep(neutralityMinimumA, 1.0, neutrality);
                float maskA = lightMaskA * neutralMaskA * strengthA;

                // Faixa B: brancos avermelhados (G/B altos e proximos; R moderadamente acima deles).
                float minGB = min(rgb.g, rgb.b);
                float gbSpread = abs(rgb.g - rgb.b);
                float redPush = rgb.r - minGB;
                float lightMaskB = smoothstep(thresholdB - featherB, thresholdB + featherB, minGB);
                float rednessMaskB = smoothstep(0.0, redSensitivityB * 0.4, redPush)
                    * (1.0 - smoothstep(redSensitivityB, redSensitivityB * 1.6, redPush));
                float lowSaturationMaskB = 1.0 - clamp(gbSpread / max(minGB, 0.001), 0.0, 1.0);
                float maskB = lightMaskB * rednessMaskB * lowSaturationMaskB * strengthB;
                maskB *= (1.0 - maskA); // Faixa A tem prioridade onde as duas se sobrepoem.

                vec3 blendedTarget = (maskA + maskB) > 0.0001
                    ? (targetColorA * maskA + targetColorB * maskB) / (maskA + maskB)
                    : targetColorA;
                float mask = clamp(maskA + maskB, 0.0, 1.0);

                // Preservacao de luminosidade: evita achatar branco/250/240/220 na mesma cor.
                float targetLuma = dot(blendedTarget, vec3(0.2126, 0.7152, 0.0722));
                vec3 scaledTarget = targetLuma > 0.001
                    ? blendedTarget * clamp(luma / targetLuma, 0.0, 1.6)
                    : blendedTarget;
                vec3 finalTarget = mix(blendedTarget, scaledTarget, luminancePreservation);

                vec3 outRgb = mix(rgb, finalTarget, mask);

                // Brilho maximo dos tons claros: reduz a luminosidade final sem trocar a tonalidade.
                float outLuma = dot(outRgb, vec3(0.2126, 0.7152, 0.0722));
                if (outLuma > maxBrightness && outLuma > 0.001) {
                    outRgb *= maxBrightness / outLuma;
                }

                return default_post_processing(vec4(outRgb, c.a));
            }
            """);

        return sb.ToString();
    }
}
