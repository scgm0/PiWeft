using Markdig;
using Markdig.Renderers;
using Markdig.Renderers.Html;
using Markdig.Renderers.Html.Inlines;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace PiWeft.Cli;

/// 导出用的 Markdown → HTML。会话正文是 Markdown，导出后应当和编辑器里看到的一样可读；
/// 直接塞进 <c>&lt;pre&gt;</c> 会把标题、列表、表格和代码块当成字面量。
/// 原始 HTML 与脚本式链接不渲染：导出文件不执行会话内容里的任何东西。
internal static class MarkdownHtml
{
	private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
		.UseAdvancedExtensions()
		.DisableHtml()
		.Build();

	public static string Render(string markdown)
	{
		if (string.IsNullOrWhiteSpace(markdown))
		{
			return string.Empty;
		}

		using var writer = new StringWriter();
		var renderer = new HtmlRenderer(writer);
		Pipeline.Setup(renderer);
		renderer.ObjectRenderers.Replace<LinkInlineRenderer>(new SafeLinkRenderer());
		renderer.Render(Markdown.Parse(markdown, Pipeline));
		writer.Flush();
		return writer.ToString();
	}

	/// 只放行 web 与 mailto 链接。相对链接与锚点在单文件导出里没有意义，按文本处理。
	internal static bool IsSafeUrl(string? url)
	{
		if (string.IsNullOrWhiteSpace(url))
		{
			return false;
		}

		return Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
			(uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeMailto);
	}

	private sealed class SafeLinkRenderer : LinkInlineRenderer
	{
		protected override void Write(HtmlRenderer renderer, LinkInline link)
		{
			if (!IsSafeUrl(link.Url))
			{
				// 链接文字仍然显示，只是不再是可点的链接。
				renderer.WriteChildren(link);
				return;
			}

			base.Write(renderer, link);
		}
	}
}
