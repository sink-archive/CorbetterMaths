using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Majestic12;

// Code originally from StackOverflow, with some refactorings

namespace CorbetterMaths
{
	public class Majestic12ToXml
	{
		private static readonly Regex StartsAsNumeric = new Regex(@"^[0-9]", RegexOptions.Compiled);

		private static readonly Regex WeirdTag = new Regex(@"^<!\[.*\]>$"); // matches "<![if !supportEmptyParas]>"
		private static readonly Regex AspnetPrecompiled = new Regex(@"^<%.*%>$"); // matches "<%@ ... %>"
		private static readonly Regex ShortHtmlComment = new Regex(@"^<!-.*->$"); // matches "<!-Extra_Images->"

		public static IEnumerable<XNode> ConvertNodesToXml(byte[] htmlAsBytes)
		{
			var parser = OpenParser();
			parser.Init(htmlAsBytes);

			var currentNode = new XElement("document");

			HTMLchunk m12Chunk = null;

			var xmlnsAttributeIndex = 0;
			var originalHtml        = string.Empty;

			while ((m12Chunk = parser.ParseNext()) != null)
				try
				{
					Debug.Assert(!m12Chunk.bHashMode); // popular default for Majestic-12 setting

					XNode    newNode        = null;
					XElement newNodesParent = null;

					switch (m12Chunk.oType)
					{
						case HTMLchunkType.OpenTag:

							// Tags are added as a child to the current tag, 
							// except when the new tag implies the closure of 
							// some number of ancestor tags.

							newNode = ParseTagNode(m12Chunk, originalHtml, ref xmlnsAttributeIndex);

							if (newNode != null)
							{
								currentNode = FindParentOfNewNode(m12Chunk, originalHtml, currentNode);

								newNodesParent = currentNode;

								newNodesParent.Add(newNode);

								currentNode = newNode as XElement;
							}

							break;

						case HTMLchunkType.CloseTag:

							if (m12Chunk.bEndClosure)
							{
								newNode = ParseTagNode(m12Chunk, originalHtml, ref xmlnsAttributeIndex);

								if (newNode != null)
								{
									currentNode = FindParentOfNewNode(m12Chunk, originalHtml, currentNode);

									newNodesParent = currentNode;
									newNodesParent.Add(newNode);
								}
							}
							else
							{
								var nodeToClose = currentNode;

								var m12ChunkCleanedTag = CleanupTagName(m12Chunk.sTag, originalHtml);

								while (nodeToClose != null && nodeToClose.Name.LocalName != m12ChunkCleanedTag)
									nodeToClose = nodeToClose.Parent;

								if (nodeToClose != null)
									currentNode = nodeToClose.Parent;

								Debug.Assert(currentNode != null);
							}

							break;

						case HTMLchunkType.Script:

							newNode        = new XElement("script", "REMOVED");
							newNodesParent = currentNode;
							newNodesParent.Add(newNode);
							break;

						case HTMLchunkType.Comment:

							newNodesParent = currentNode;

							if (m12Chunk.sTag == "!--")
								newNode = new XComment(m12Chunk.oHTML);
							else if (m12Chunk.sTag == "![CDATA[")
								newNode = new XCData(m12Chunk.oHTML);
							else
								throw new Exception("Unrecognized comment sTag");

							newNodesParent.Add(newNode);

							break;

						case HTMLchunkType.Text:

							currentNode.Add(m12Chunk.oHTML);
							break;
					}
				}
				catch (Exception e)
				{
					var wrappedE = new Exception("Error using Majestic12.HTMLChunk, reason: " + e.Message, e);

					// the original html is copied for tracing/debugging purposes
					originalHtml = new string(htmlAsBytes.Skip(m12Chunk.iChunkOffset)
					                                     .Take(m12Chunk.iChunkLength)
					                                     .Select(b => (char) b).ToArray());

					wrappedE.Data.Add("source", originalHtml);

					throw wrappedE;
				}

			while (currentNode.Parent != null)
				currentNode = currentNode.Parent;

			return currentNode.Nodes();
		}

		private static XElement FindParentOfNewNode(HTMLchunk m12Chunk, string originalHtml,
		                                            XElement  nextPotentialParent)
		{
			var m12ChunkCleanedTag = CleanupTagName(m12Chunk.sTag, originalHtml);

			XElement discoveredParent = null;

			// Get a list of all ancestors
			var ancestors = new List<XElement>();
			var ancestor  = nextPotentialParent;
			while (ancestor != null)
			{
				ancestors.Add(ancestor);
				ancestor = ancestor.Parent;
			}

			switch (m12ChunkCleanedTag)
			{
				// Check if the new tag implies a previous tag was closed.
				case "form":
					discoveredParent = ancestors
					                  .Where(xe => m12ChunkCleanedTag == xe.Name)
					                  .Take(1)
					                  .Select(xe => xe.Parent)
					                  .FirstOrDefault();
					break;
				case "td":
					discoveredParent = ancestors
					                  .TakeWhile(xe => xe.Name        != "tr")
					                  .Where(xe => m12ChunkCleanedTag == xe.Name)
					                  .Take(1)
					                  .Select(xe => xe.Parent)
					                  .FirstOrDefault();
					break;
				case "tr":
					discoveredParent = ancestors
					                  .TakeWhile(xe => !(xe.Name == "table"
					                                  || xe.Name == "thead"
					                                  || xe.Name == "tbody"
					                                  || xe.Name != "tfoot"))
					                  .Where(xe => m12ChunkCleanedTag == xe.Name)
					                  .Take(1)
					                  .Select(xe => xe.Parent)
					                  .FirstOrDefault();
					break;
				case "thead":
				case "tbody":
				case "tfoot":
					discoveredParent = ancestors
					                  .TakeWhile(xe => xe.Name        != "table")
					                  .Where(xe => m12ChunkCleanedTag == xe.Name)
					                  .Take(1)
					                  .Select(xe => xe.Parent)
					                  .FirstOrDefault();
					break;
			}

			return discoveredParent ?? nextPotentialParent;
		}

		private static string CleanupTagName(string originalName, string originalHtml)
		{
			var tagName = originalName;

			tagName = tagName.TrimStart(new[] {'?'}); // for nodes <?xml >

			if (tagName.Contains(':'))
				tagName = tagName.Substring(tagName.LastIndexOf(':') + 1);

			return tagName;
		}

		private static bool TryCleanupAttributeName(string originalName, ref int xmlnsIndex, out string result)
		{
			result = null;
			var attributeName = originalName;

			if (string.IsNullOrEmpty(originalName))
				return false;

			if (StartsAsNumeric.IsMatch(originalName))
				return false;

			//
			// transform xmlns attributes so they don't actually create any XML namespaces
			//
			if (attributeName.ToLower().Equals("xmlns"))
			{
				attributeName = "xmlns_" + xmlnsIndex;
				xmlnsIndex++;
			}
			else
			{
				if (attributeName.ToLower().StartsWith("xmlns:"))
					attributeName = "xmlns_" + attributeName.Substring("xmlns:".Length);

				//
				// trim trailing \"
				//
				attributeName = attributeName.TrimEnd(new[] {'\"'});

				attributeName = attributeName.Replace(":", "_");
			}

			result = attributeName;

			return true;
		}

		private static XElement ParseTagNode(HTMLchunk m12Chunk, string originalHtml, ref int xmlnsIndex)
		{
			if (string.IsNullOrEmpty(m12Chunk.sTag))
			{
				if (m12Chunk.sParams.Length > 0 && m12Chunk.sParams[0].ToLower().Equals("doctype"))
					return new XElement("doctype");

				if (WeirdTag.IsMatch(originalHtml))
					return new XElement("REMOVED_weirdBlockParenthesisTag");

				if (AspnetPrecompiled.IsMatch(originalHtml))
					return new XElement("REMOVED_ASPNET_PrecompiledDirective");

				if (ShortHtmlComment.IsMatch(originalHtml))
					return new XElement("REMOVED_ShortHtmlComment");

				// Nodes like "<br <br>" will end up with a m12chunk.sTag==""...  We discard these nodes.
				return null;
			}

			var tagName = CleanupTagName(m12Chunk.sTag, originalHtml);

			var result = new XElement(tagName);

			var attributes = new List<XAttribute>();

			for (var i = 0; i < m12Chunk.iParams; i++)
			{
				if (m12Chunk.sParams[i] == "<!--")
				{
					// an HTML comment was embedded within a tag.  This comment and its contents
					// will be interpreted as attributes by Majestic-12... skip this attributes
					for (; i < m12Chunk.iParams; i++)
						if (m12Chunk.sTag == "--" || m12Chunk.sTag == "-->")
							break;

					continue;
				}

				if (m12Chunk.sParams[i] == "?" && string.IsNullOrEmpty(m12Chunk.sValues[i]))
					continue;

				var attributeName = m12Chunk.sParams[i];

				if (!TryCleanupAttributeName(attributeName, ref xmlnsIndex, out attributeName))
					continue;

				attributes.Add(new XAttribute(attributeName, m12Chunk.sValues[i]));
			}

			// If attributes are duplicated with different values, we complain.
			// If attributes are duplicated with the same value, we remove all but 1.
			var duplicatedAttributes = attributes.GroupBy(a => a.Name).Where(g => g.Count() > 1);

			foreach (var duplicatedAttribute in duplicatedAttributes)
			{
				if (duplicatedAttribute.GroupBy(da => da.Value).Count() > 1)
					throw new Exception("Attribute value was given different values");

				attributes.RemoveAll(a => a.Name == duplicatedAttribute.Key);
				attributes.Add(duplicatedAttribute.First());
			}

			result.Add(attributes);

			return result;
		}

		private static HTMLparser OpenParser()
		{
			var oP = new HTMLparser();

			// The code+comments in this function are from the Majestic-12 sample documentation.

			// ...

			// This is optional, but if you want high performance then you may
			// want to set chunk hash mode to FALSE. This would result in tag params
			// being added to string arrays in HTMLchunk object called sParams and sValues, with number
			// of actual params being in iParams. See code below for details.
			//
			// When TRUE (and its default) tag params will be added to hashtable HTMLchunk (object).oParams
			oP.SetChunkHashMode(false);

			// if you set this to true then original parsed HTML for given chunk will be kept - 
			// this will reduce performance somewhat, but may be desireable in some cases where
			// reconstruction of HTML may be necessary
			oP.bKeepRawHTML = false;

			// if set to true (it is false by default), then entities will be decoded: this is essential
			// if you want to get strings that contain final representation of the data in HTML, however
			// you should be aware that if you want to use such strings into output HTML string then you will
			// need to do Entity encoding or same string may fail later
			oP.bDecodeEntities = true;

			// we have option to keep most entities as is - only replace stuff like &nbsp; 
			// this is called Mini Entities mode - it is handy when HTML will need
			// to be re-created after it was parsed, though in this case really
			// entities should not be parsed at all
			oP.bDecodeMiniEntities = true;

			if (!oP.bDecodeEntities && oP.bDecodeMiniEntities)
				oP.InitMiniEntities();

			// if set to true, then in case of Comments and SCRIPT tags the data set to oHTML will be
			// extracted BETWEEN those tags, rather than include complete RAW HTML that includes tags too
			// this only works if auto extraction is enabled
			oP.bAutoExtractBetweenTagsOnly = true;

			// if true then comments will be extracted automatically
			oP.bAutoKeepComments = true;

			// if true then scripts will be extracted automatically: 
			oP.bAutoKeepScripts = true;

			// if this option is true then whitespace before start of tag will be compressed to single
			// space character in string: " ", if false then full whitespace before tag will be returned (slower)
			// you may only want to set it to false if you want exact whitespace between tags, otherwise it is just
			// a waste of CPU cycles
			oP.bCompressWhiteSpaceBeforeTag = true;

			// if true (default) then tags with attributes marked as CLOSED (/ at the end) will be automatically
			// forced to be considered as open tags - this is no good for XML parsing, but I keep it for backwards
			// compatibility for my stuff as it makes it easier to avoid checking for same tag which is both closed
			// or open
			oP.bAutoMarkClosedTagsWithParamsAsOpen = false;

			return oP;
		}
	}
}