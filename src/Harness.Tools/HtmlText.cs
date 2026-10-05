using System.Text;
using AngleSharp.Dom;
using AngleSharp.Html.Dom;
using AngleSharp.Html.Parser;

namespace Harness.Tools;

/// <summary>
/// Turns an HTML page into Markdown-like text for the model: the main content only (scripts, styles, navigation and
/// forms' controls dropped), headings, lists, tables as <c>a | b</c> rows, code fences, and links as <c>[text](url)</c>.
/// </summary>
internal static class HtmlText
{
    private static readonly HashSet<string> Dropped =
        ["head", "script", "style", "noscript", "template", "svg", "math", "canvas", "iframe", "object", "embed", "img", "picture",
         "video", "audio", "button", "input", "select", "textarea", "dialog", "nav", "aside", "footer"];

    private static readonly HashSet<string> Paragraphs = ["p", "blockquote", "table", "figure", "dl", "details", "address"];

    private static readonly HashSet<string> Lines =
        ["div", "section", "article", "main", "header", "form", "fieldset", "figcaption", "caption", "summary", "dt", "dd",
         "thead", "tbody", "tfoot", "tr", "center"];

    public static IHtmlDocument Parse(string html) => new HtmlParser().ParseDocument(html);

    public static (string? Title, string Text) Convert(string html, Uri? baseUri)
    {
        IHtmlDocument doc = Parse(html);
        string? title = string.IsNullOrWhiteSpace(doc.Title) ? null : Collapse(doc.Title);
        if (doc.QuerySelector("base[href]")?.GetAttribute("href") is { } baseHref && Uri.TryCreate(baseUri, baseHref, out Uri? declared))
            baseUri = declared;

        IElement? root = doc.QuerySelector("main") ?? doc.QuerySelector("[role=main]")
            ?? (doc.QuerySelectorAll("article") is { Length: 1 } articles ? articles[0] : null) ?? doc.Body;
        if (root is null) return (title, "");

        foreach (IElement e in root.QuerySelectorAll("*").ToList())
        {
            // A page header is site chrome; a header inside an article or section holds its title.
            bool chrome = Dropped.Contains(e.LocalName) || e.LocalName == "header" && e.ParentElement?.Closest("article, section, main") is null;
            if (chrome || e.HasAttribute("hidden") || e.GetAttribute("aria-hidden") == "true" || e.GetAttribute("role") is "navigation" or "banner" or "contentinfo")
                e.Remove();
        }

        Writer writer = new(baseUri);
        writer.Children(root);
        return (title, writer.ToString());
    }

    /// <summary>Text with every run of whitespace turned into one space.</summary>
    public static string Collapse(string text) => string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private sealed class Writer(Uri? baseUri)
    {
        private readonly StringBuilder _sb = new();
        private readonly Stack<int> _lists = new();   // the next number of each open ordered list, or -1 for bullets
        private bool _space;
        private int _cells;   // inside a table cell every break is a space, so a row stays on one line

        public override string ToString() => _sb.ToString().Trim();

        public void Children(INode node)
        {
            foreach (INode child in node.ChildNodes)
            {
                if (child is IText text) Text(text.Data);
                else if (child is IElement element) Element(element);
            }
        }

        private void Element(IElement e)
        {
            switch (e.LocalName)
            {
                case "br":
                    Break(1);
                    break;
                case "hr":
                    Break(2);
                    Raw("---");
                    Break(2);
                    break;
                case "h1" or "h2" or "h3" or "h4" or "h5" or "h6":
                    Break(2);
                    Raw(new string('#', e.LocalName[1] - '0') + " ");
                    Children(e);
                    Break(2);
                    break;
                case "pre":
                    Break(2);
                    Raw("```");
                    Break(1);
                    _sb.Append(e.TextContent.Trim('\n').TrimEnd());
                    Break(1);
                    Raw("```");
                    Break(2);
                    break;
                case "code" or "kbd" or "samp":
                    Raw("`" + Collapse(e.TextContent) + "`");
                    break;
                case "a":
                    Link(e);
                    break;
                case "ul" or "ol":
                    Break(_lists.Count == 0 ? 2 : 1);
                    _lists.Push(e.LocalName == "ol" ? (int.TryParse(e.GetAttribute("start"), out int start) ? start : 1) : -1);
                    Children(e);
                    _lists.Pop();
                    Break(_lists.Count == 0 ? 2 : 1);
                    break;
                case "li":
                    Break(1);
                    int depth = Math.Max(0, _lists.Count - 1);
                    string marker = "- ";
                    if (_lists.TryPop(out int next))
                    {
                        if (next >= 0) { marker = next + ". "; next++; }
                        _lists.Push(next);
                    }
                    Raw(new string(' ', depth * 2) + marker);
                    Children(e);
                    Break(1);
                    break;
                case "td" or "th":
                    if (e.PreviousElementSibling is not null)
                    {
                        _space = false;
                        Raw(" |");
                        _space = true;
                    }
                    _cells++;
                    Children(e);
                    _cells--;
                    break;
                default:
                    int breaks = Paragraphs.Contains(e.LocalName) ? 2 : Lines.Contains(e.LocalName) ? 1 : 0;
                    Break(breaks);
                    Children(e);
                    Break(breaks);
                    break;
            }
        }

        private void Link(IElement a)
        {
            string? href = a.GetAttribute("href");
            if (string.IsNullOrWhiteSpace(href) || href.StartsWith('#')
                || !Uri.TryCreate(baseUri, href.Trim(), out Uri? target) || target.Scheme is not ("http" or "https"))
            {
                Children(a);
                return;
            }
            int mark = _sb.Length;
            Raw("[");
            int start = _sb.Length;
            Children(a);
            TrimSpaces();
            if (_sb.Length == start)
            {
                _sb.Length = mark;   // a link around an image: nothing to show
                return;
            }
            _sb.Append("](").Append(target.AbsoluteUri).Append(')');
        }

        private void Text(string text)
        {
            foreach (char c in text)
            {
                if (char.IsWhiteSpace(c))
                {
                    _space = true;
                    continue;
                }
                Raw(c.ToString());
            }
        }

        private void Raw(string text)
        {
            if (_space && _sb.Length > 0 && _sb[^1] is not ('\n' or ' ' or '[')) _sb.Append(' ');
            _space = false;
            _sb.Append(text);
        }

        /// <summary>Makes the text end in at least <paramref name="count"/> newlines (none at the very start).</summary>
        private void Break(int count)
        {
            if (count == 0) return;
            if (_cells > 0)
            {
                _space = true;
                return;
            }
            _space = false;
            TrimSpaces();
            if (_sb.Length == 0) return;
            int have = 0;
            while (have < _sb.Length && _sb[_sb.Length - 1 - have] == '\n') have++;
            for (; have < count; have++) _sb.Append('\n');
        }

        private void TrimSpaces()
        {
            while (_sb.Length > 0 && _sb[^1] == ' ') _sb.Length--;
        }
    }
}
