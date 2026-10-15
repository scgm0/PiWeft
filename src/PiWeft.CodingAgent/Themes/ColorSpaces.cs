namespace PiWeft.CodingAgent.Themes;

/// <summary>
/// OKLab / OKHSL / sRGB 互转。移植自 Björn Ottosson 的参考实现
/// （<c>https://bottosson.github.io/posts/colorpicker/</c>，MIT），与 pi 的
/// <c>packages/tui/src/oklab.ts</c> 使用同一组常量，保证同一主题在两侧解析出相同颜色。
/// </summary>
internal static class ColorSpaces
{
	// Oklab 的 (L, a, b) → cube-root LMS 的系数（第 0 列固定为 1）。
	private const double LabToLmsA0 = 0.3963377773761749;
	private const double LabToLmsA1 = -0.1055613458156586;
	private const double LabToLmsA2 = -0.0894841775298119;
	private const double LabToLmsB0 = 0.2158037573099136;
	private const double LabToLmsB1 = -0.0638541728258133;
	private const double LabToLmsB2 = -1.2914855480194092;

	private const double K1 = 0.206;
	private const double K2 = 0.03;
	private const double K3 = (1 + K1) / (1 + K2);

	/// <summary>OKHSL 亮度 → Oklab 亮度。</summary>
	private static double OkhslToOklabLightness(double x) => (x * x + K1 * x) / (K3 * (x + K2));

	/// <summary>sRGB 传递函数：线性 → 编码，取值 0-1。</summary>
	private static double LinearToSrgb(double value) =>
		value > 0.0031308 ? (1.055 * Math.Pow(value, 1 / 2.4)) - 0.055 : 12.92 * value;

	/// <summary>
	/// 每通道（红、绿、蓝）在何处先越界的 (a, b) 半平面，以及该处最大饱和度的多项式拟合。
	/// 与 pi 的 <c>SATURATION_FIT</c> 一致。
	/// </summary>
	private static readonly ((double X, double Y) HalfPlane, double[] Fit)[] SaturationFit =
	[
		((-1.8817031, -0.80936501), [1.19086277, 1.76576728, 0.59662641, 0.75515197, 0.56771245]),
		((1.8144408, -1.19445267), [0.73956515, -0.45954404, 0.08285427, 0.12541073, -0.14503204]),
		((0.13110758, 1.81333971), [1.35733652, -0.00915799, -1.1513021, -0.50559606, 0.00692167]),
	];

	/// <summary>Oklab (L, a, b) → 线性 sRGB，可能越界。</summary>
	public static (double R, double G, double B) OklabToLinearSrgb(double l, double a, double b)
	{
		var l_ = l + (LabToLmsA0 * a) + (LabToLmsB0 * b);
		var m_ = l + (LabToLmsA1 * a) + (LabToLmsB1 * b);
		var s_ = l + (LabToLmsA2 * a) + (LabToLmsB2 * b);
		var lc = l_ * l_ * l_;
		var mc = m_ * m_ * m_;
		var sc = s_ * s_ * s_;

		return (
			(4.0767416360759583 * lc) + (-3.3077115392580629 * mc) + (0.2309699031821043 * sc),
			(-1.2684379732850315 * lc) + (2.6097573492876882 * mc) + (-0.341319376002657 * sc),
			(-0.0041960761386756 * lc) + (-0.7034186179359362 * mc) + (1.7076146940746117 * sc));
	}

	/// <summary>线性 sRGB → sRGB 通道（0-255，四舍五入，越界裁剪）。</summary>
	public static ThemeColor LinearSrgbToColor(double r, double g, double b)
	{
		var clamped = (
			Math.Clamp(LinearToSrgb(r), 0, 1),
			Math.Clamp(LinearToSrgb(g), 0, 1),
			Math.Clamp(LinearToSrgb(b), 0, 1));
		return new ThemeColor(
			(byte)Math.Round(clamped.Item1 * 255),
			(byte)Math.Round(clamped.Item2 * 255),
			(byte)Math.Round(clamped.Item3 * 255));
	}

	/// <summary>
	/// OKLCH → sRGB 颜色。亮度 0-1，色度 ≥ 0，色相为角度。
	/// 越出色域时按 pi 的做法映射：固定色相，二分收缩色度直到落回 sRGB；
	/// 无彩色始终在色域内，因此 <c>oklch(100% 0.3 150)</c> 会得到白色。
	/// </summary>
	public static ThemeColor OklchToColor(double lightness, double chroma, double hue)
	{
		var radians = hue * Math.PI / 180.0;
		var cos = Math.Cos(radians);
		var sin = Math.Sin(radians);

		var direct = OklabToLinearSrgb(lightness, chroma * cos, chroma * sin);
		if (IsInSrgbGamut(direct))
		{
			return LinearSrgbToColor(direct.R, direct.G, direct.B);
		}

		var linear = OklabToLinearSrgb(lightness, 0, 0);
		double low = 0;
		var high = chroma;
		for (var index = 0; index < 20; index++)
		{
			var middle = (low + high) / 2;
			var candidate = OklabToLinearSrgb(lightness, middle * cos, middle * sin);
			if (IsInSrgbGamut(candidate))
			{
				low = middle;
				linear = candidate;
			}
			else
			{
				high = middle;
			}
		}

		return LinearSrgbToColor(linear.R, linear.G, linear.B);
	}

	private static bool IsInSrgbGamut((double R, double G, double B) linear)
	{
		const double epsilon = 1e-7;
		return linear.R >= -epsilon && linear.R <= 1 + epsilon
			&& linear.G >= -epsilon && linear.G <= 1 + epsilon
			&& linear.B >= -epsilon && linear.B <= 1 + epsilon;
	}

	/// <summary>每个 cube-root LMS 分量沿色度方向 (a, b) 的变化率。</summary>
	private static (double L, double M, double S) LmsSlopes(double a, double b) => (
		(LabToLmsA0 * a) + (LabToLmsB0 * b),
		(LabToLmsA1 * a) + (LabToLmsB1 * b),
		(LabToLmsA2 * a) + (LabToLmsB2 * b));

	/// <summary>色相 (a, b) 在 sRGB 内的最大饱和度（C/L）：多项式拟合加一步 Halley 迭代。</summary>
	private static double MaxSaturation(double a, double b)
	{
		var channel = 2;
		for (var index = 0; index < SaturationFit.Length; index++)
		{
			var (x, y) = SaturationFit[index].HalfPlane;
			if ((x * a) + (y * b) > 1)
			{
				channel = index;
				break;
			}
		}

		var fit = SaturationFit[channel].Fit;
		var saturation = fit[0] + (fit[1] * a) + (fit[2] * b) + (fit[3] * a * a) + (fit[4] * a * b);

		var slopes = LmsSlopes(a, b);
		var baseL = 1 + (saturation * slopes.L);
		var baseM = 1 + (saturation * slopes.M);
		var baseS = 1 + (saturation * slopes.S);

		var (wr, wg, wb) = channel switch
		{
			0 => (4.0767416360759583, -3.3077115392580629, 0.2309699031821043),
			1 => (-1.2684379732850315, 2.6097573492876882, -0.341319376002657),
			_ => (-0.0041960761386756, -0.7034186179359362, 1.7076146940746117),
		};

		var f = (wr * Math.Pow(baseL, 3)) + (wg * Math.Pow(baseM, 3)) + (wb * Math.Pow(baseS, 3));
		var f1 = (wr * 3 * slopes.L * baseL * baseL) + (wg * 3 * slopes.M * baseM * baseM) + (wb * 3 * slopes.S * baseS * baseS);
		var f2 = (wr * 6 * slopes.L * slopes.L * baseL) + (wg * 6 * slopes.M * slopes.M * baseM) + (wb * 6 * slopes.S * slopes.S * baseS);

		return saturation - ((f * f1) / ((f1 * f1) - (0.5 * f * f2)));
	}

	/// <summary>色相 (a, b) 上 sRGB 最饱和颜色的 Oklab 亮度与色度。</summary>
	private static (double Lightness, double Chroma) Cusp(double a, double b)
	{
		var saturation = MaxSaturation(a, b);
		var linear = OklabToLinearSrgb(1, saturation * a, saturation * b);
		var lightness = Math.Cbrt(1 / Math.Max(Math.Max(linear.R, linear.G), linear.B));
		return (lightness, lightness * saturation);
	}

	/// <summary>固定亮度线上离开 sRGB 色域的色度。</summary>
	private static double MaxChroma(double a, double b, double lightness, (double Lightness, double Chroma) cusp)
	{
		if (lightness <= cusp.Lightness)
		{
			return cusp.Chroma * lightness / cusp.Lightness;
		}

		var t = cusp.Chroma * (lightness - 1) / (cusp.Lightness - 1);
		var slopes = LmsSlopes(a, b);
		var l = Math.Pow(lightness + (t * slopes.L), 3);
		var m = Math.Pow(lightness + (t * slopes.M), 3);
		var s = Math.Pow(lightness + (t * slopes.S), 3);

		var first = (
			(3 * slopes.L * Math.Pow(lightness + (t * slopes.L), 2)),
			(3 * slopes.M * Math.Pow(lightness + (t * slopes.M), 2)),
			(3 * slopes.S * Math.Pow(lightness + (t * slopes.S), 2)));
		var second = (
			(6 * slopes.L * slopes.L * (lightness + (t * slopes.L))),
			(6 * slopes.M * slopes.M * (lightness + (t * slopes.M))),
			(6 * slopes.S * slopes.S * (lightness + (t * slopes.S))));

		var best = double.MaxValue;
		foreach (var (w0, w1, w2) in Rows())
		{
			var f = (w0 * l) + (w1 * m) + (w2 * s) - 1;
			var f1 = (w0 * first.Item1) + (w1 * first.Item2) + (w2 * first.Item3);
			var f2 = (w0 * second.Item1) + (w1 * second.Item2) + (w2 * second.Item3);
			var u = f1 / ((f1 * f1) - (0.5 * f * f2));
			if (u >= 0)
			{
				best = Math.Min(best, t - (f * u));
			}
		}

		return best;

		static IEnumerable<(double W0, double W1, double W2)> Rows()
		{
			yield return (4.0767416360759583, -3.3077115392580629, 0.2309699031821043);
			yield return (-1.2684379732850315, 2.6097573492876882, -0.341319376002657);
			yield return (-0.0041960761386756, -0.7034186179359362, 1.7076146940746117);
		}
	}

	/// <summary>OKHSL 在亮度 L 与色相 (a, b) 下的三个色度参考点：[c0, cMid, cMax]。</summary>
	private static (double C0, double CMid, double CMax) ChromaStops(double lightness, double a, double b)
	{
		var peak = Cusp(a, b);
		var cMax = MaxChroma(a, b, lightness, peak);
		var k = cMax / Math.Min(lightness * (peak.Chroma / peak.Lightness), (1 - lightness) * (peak.Chroma / (1 - peak.Lightness)));

		var midS =
			0.11516993 +
			1 / (7.4477897 + (4.1590124 * b) + (a * (-2.19557347 + (1.75198401 * b) + (a * (-2.13704948 - (10.02301043 * b) + (a * (-4.24894561 + (5.38770819 * b) + (4.69891013 * a))))))));
		var midT =
			0.11239642 +
			1 / (1.6132032 - (0.68124379 * b) + (a * (0.40370612 + (0.90148123 * b) + (a * (-0.27087943 + (0.6122399 * b) + (a * (0.00299215 - (0.45399568 * b) - (0.14661872 * a))))))));

		var cMid = 0.9 * k * Math.Sqrt(Math.Sqrt(
			1 / (Math.Pow(1 / (lightness * midS), 4) + Math.Pow(1 / ((1 - lightness) * midT), 4))));
		var c0 = Math.Sqrt(1 / (Math.Pow(1 / (lightness * 0.4), 2) + Math.Pow(1 / ((1 - lightness) * 0.8), 2)));
		return (c0, cMid, cMax);
	}

	/// <summary>OKHSL → sRGB 颜色。色相为角度，饱和度与亮度 0-1。</summary>
	public static ThemeColor OkhslToColor(double hue, double saturation, double lightness)
	{
		var labLightness = OkhslToOklabLightness(lightness);
		double a = 0;
		double b = 0;

		if (labLightness is > 0 and < 1 && saturation > 0)
		{
			var angle = 2 * Math.PI * (((hue % 360) + 360) % 360) / 360;
			a = Math.Cos(angle);
			b = Math.Sin(angle);
			var (c0, cMid, cMax) = ChromaStops(labLightness, a, b);

			// 色度从 0 经 cMid（s = 0.8）升到 cMax（s = 1）。
			double chroma;
			if (saturation < 0.8)
			{
				var t = 1.25 * saturation;
				var k0 = 0.8 * c0;
				chroma = t * k0 / (1 - ((1 - (k0 / cMid)) * t));
			}
			else
			{
				var t = 5 * (saturation - 0.8);
				var k0 = 0.2 * cMid * cMid * 1.25 * 1.25 / c0;
				chroma = cMid + (t * k0 / (1 - ((1 - (k0 / (cMax - cMid))) * t)));
			}

			a *= chroma;
			b *= chroma;
		}

		var linear = OklabToLinearSrgb(labLightness, a, b);
		return LinearSrgbToColor(linear.R, linear.G, linear.B);
	}
}
