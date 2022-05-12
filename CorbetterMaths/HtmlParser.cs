using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading.Tasks;
using HtmlAgilityPack;

namespace CorbetterMaths;

public static class HtmlParser
{
	public static async Task<Content[]> ParseUrl(string url)
		=> ParseHtml(await new HttpClient().GetStreamAsync(url));
	
	private static Content[] ParseHtml(Stream html)
	{
		var doc = new HtmlDocument();
		doc.Load(html);

		var contentItems = doc.QuerySelectorAll(".entry-content p");

		var query = from item in contentItems
					select item.ChildNodes.Where(n => n.ParentNode == item && (n as HtmlTextNode)?.Text.Trim() != "")
							   .ToArray()
					into innerDescendants
					where IsValidDescendant(innerDescendants)
					select DescendantsToContent(innerDescendants);

		return query.ToArray();
	}

	private static Content DescendantsToContent(IReadOnlyCollection<HtmlNode> descendants)
	{
		var textNode = (HtmlTextNode) descendants.First(n => n is HtmlTextNode);
		var links    = descendants.Where(n => n.Name == "a").ToArray();

		if (links[0].Descendants().First() is not HtmlTextNode linkText)
			throw new Exception("linkText was not found properly");


		var linkRefs = links.Select(l => l.Attributes["href"].Value).ToArray();

		// ElementAtOrDefault returns null if out of range
		var videoNumber  = linkText.Text[6..];
		var videoLink    = linkRefs[0];
		var practiceLink = linkRefs.ElementAtOrDefault(1);
		var textbookLink = linkRefs.ElementAtOrDefault(2);
		var topic        = textNode.Text.Trim();

		return new Content(videoNumber, topic, videoLink, practiceLink, textbookLink, null);
	}

	private static bool IsValidDescendant(IReadOnlyCollection<HtmlNode> descendants)
	{
		// must be 2 - 4 inclusive nodes
		if (descendants.Count is < 2 or > 4) return false;

		// must be 1 - 3 inclusive links
		var links = descendants.Where(d => d.Name == "a").ToArray();
		if (links.Length is < 1 or > 3) return false;

		// must be one with "Video"
		if (!links.Any(d => d.FirstChild is HtmlTextNode htn && htn.Text.TrimStart().StartsWith("Video ")))
			return false;

		// must only be one text node
		return descendants.Count(n => n.NodeType == HtmlNodeType.Text) == 1;
	}
}