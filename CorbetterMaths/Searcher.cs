using System;
using System.Collections.Generic;
using System.Linq;
using FastFuzzyStringMatcher;

namespace CorbetterMaths;

public class Searcher
{
	private const int SearchLenience = 15;

	private const float SearchThreshold = 30;

	private readonly StringMatcher<Content> _matcher = new();

	private readonly Content[] _contents;

	public Searcher(Content[] items)
	{
		_contents = items;

		foreach (var item in items)
			_matcher.Add(item.Topic, item);
	}

	public Content[] SearchByNum(string num) => _contents.Where(c => c.Number == num).ToArray();

	private IEnumerable<Content> SearchByTopicSimple(string topic)
		=> _contents.Where(c => c.Topic.Contains(topic, StringComparison.InvariantCultureIgnoreCase));

	public Content[] SearchByTopic(string topic)
		=> SearchByTopicSimple(topic)
		  .Concat(_matcher.Search(topic, SearchLenience)
						  .Where(r => r.MatchPercentage > SearchThreshold)
						  .Select(r => r.AssociatedData with { MatchPercent = Convert.ToInt32(r.MatchPercentage) }))
		  .ToArray();
}