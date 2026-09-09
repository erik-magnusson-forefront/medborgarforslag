using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using HtmlAgilityPack;

const string archiveUrl = "https://e-tjanster.are.se/forslag/archiveddata/301";
const string suggestionBaseUrl = "https://e-tjanster.are.se/forslag/show/";

var outputDirectory = Path.Combine(Environment.CurrentDirectory, "output");

for (var i = 0; i < args.Length; i++)
{
    if (string.Equals(args[i], "--output", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
    {
        outputDirectory = Path.GetFullPath(args[i + 1]);
        i++;
    }
}

Directory.CreateDirectory(outputDirectory);

using var httpClient = new HttpClient();
httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("ForefrontAreScraper/1.0");
httpClient.Timeout = TimeSpan.FromSeconds(30);

var scraper = new AreSuggestionScraper(httpClient, archiveUrl, suggestionBaseUrl);

Console.OutputEncoding = Encoding.UTF8;
Console.WriteLine($"Fetching archived suggestions from {archiveUrl}");

var summaries = await scraper.GetSuggestionSummariesAsync();
Console.WriteLine($"Found {summaries.Count} archived suggestions.");

var successCount = 0;
var failureCount = 0;

foreach (var summary in summaries)
{
    try
    {
        var suggestion = await scraper.GetSuggestionAsync(summary);
        var path = Path.Combine(outputDirectory, $"{suggestion.Id}.txt");
        await File.WriteAllTextAsync(path, TextFileFormatter.Format(suggestion), Encoding.UTF8);
        successCount++;
        Console.WriteLine($"Saved {suggestion.Id}.txt");
    }
    catch (Exception exception)
    {
        failureCount++;
        Console.Error.WriteLine($"Failed to process {summary.Id}: {exception.Message}");
    }
}

Console.WriteLine($"Finished. Saved {successCount} files to {outputDirectory}. Failed: {failureCount}.");

sealed class AreSuggestionScraper(HttpClient httpClient, string archiveUrl, string suggestionBaseUrl)
{
    public async Task<IReadOnlyList<SuggestionSummary>> GetSuggestionSummariesAsync()
    {
        var payload = await GetPageHtmlAsync(archiveUrl);

        using var document = JsonDocument.Parse(payload);
        if (!document.RootElement.TryGetProperty("rows", out var rowsElement) || rowsElement.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException("Archive payload has an unexpected format.");
        }

        var summaries = new List<SuggestionSummary>(rowsElement.GetArrayLength());

        foreach (var row in rowsElement.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.Array || row.GetArrayLength() < 5)
            {
                continue;
            }

            var cells = row.EnumerateArray().ToArray();

            summaries.Add(new SuggestionSummary(
                cells[0].GetInt32(),
                cells[1].GetString()?.Trim() ?? string.Empty,
                cells[2].GetString()?.Trim() ?? string.Empty,
                cells[3].GetString()?.Trim() ?? string.Empty,
                cells[4].GetInt32()));
        }

        return summaries;
    }

    public async Task<SuggestionDetails> GetSuggestionAsync(SuggestionSummary summary)
    {
        var sourceUrl = $"{suggestionBaseUrl}{summary.Id}";
        var html = await GetPageHtmlAsync(sourceUrl);
        return HtmlSuggestionParser.Parse(summary.Id, sourceUrl, html);
    }

    private async Task<string> GetPageHtmlAsync(string url)
    {
        using var response = await httpClient.GetAsync(url);
        response.EnsureSuccessStatusCode();

        var bytes = await response.Content.ReadAsByteArrayAsync();
        var charset = response.Content.Headers.ContentType?.CharSet;

        Encoding encoding;
        try
        {
            encoding = string.IsNullOrWhiteSpace(charset)
                ? Encoding.GetEncoding("iso-8859-1")
                : Encoding.GetEncoding(charset.Trim('"'));
        }
        catch (ArgumentException)
        {
            encoding = Encoding.GetEncoding("iso-8859-1");
        }

        return encoding.GetString(bytes);
    }
}

static class HtmlSuggestionParser
{
    public static SuggestionDetails Parse(int id, string sourceUrl, string html)
    {
        var document = new HtmlDocument();
        document.LoadHtml(html);

        var root = document.DocumentNode.SelectSingleNode("//div[@id='flowvote']") ?? document.DocumentNode;
        var heading = NormalizeText(root.SelectSingleNode(".//h2[contains(@class, 'bigmarginbottom')]")?.InnerText);

        var labeledValues = ExtractLabeledValues(root);
        var descriptionNode = root.SelectSingleNode(".//*[@id='longDescription']");
        var description = ExtractDescription(descriptionNode);
        var attachedFiles = ExtractAttachedFiles(root);

        return new SuggestionDetails(
            id,
            heading,
            GetValue(labeledValues, "Förslaget inskickat"),
            GetValue(labeledValues, "Röstning avslutad"),
            ParseVotes(GetValue(labeledValues, "Antal röster")),
            GetValue(labeledValues, "Status"),
            description,
            attachedFiles,
            sourceUrl);
    }

    private static Dictionary<string, string> ExtractLabeledValues(HtmlNode root)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var strongNodes = root.SelectNodes(".//strong") ?? Enumerable.Empty<HtmlNode>();

        foreach (var strongNode in strongNodes)
        {
            var label = NormalizeText(strongNode.InnerText);
            if (string.IsNullOrWhiteSpace(label) || label == "Beskrivning")
            {
                continue;
            }

            var sibling = strongNode.ParentNode?.SelectSingleNode("./div");
            if (sibling is null)
            {
                sibling = strongNode.SelectSingleNode("following-sibling::div[1]");
            }

            var value = NormalizeText(sibling?.InnerText);
            if (!string.IsNullOrWhiteSpace(value))
            {
                values[label] = value;
            }
        }

        return values;
    }

    private static string ExtractDescription(HtmlNode? descriptionNode)
    {
        if (descriptionNode is null)
        {
            return string.Empty;
        }

        var clone = descriptionNode.CloneNode(true);
        var strongNode = clone.SelectSingleNode(".//strong");
        strongNode?.Remove();

        var html = clone.InnerHtml
            .Replace("<br>", "\n", StringComparison.OrdinalIgnoreCase)
            .Replace("<br/>", "\n", StringComparison.OrdinalIgnoreCase)
            .Replace("<br />", "\n", StringComparison.OrdinalIgnoreCase);

        var decoded = WebUtility.HtmlDecode(HtmlEntity.DeEntitize(html));
        var withoutTags = Regex.Replace(decoded, "<[^>]+>", string.Empty);
        return NormalizeMultilineText(withoutTags);
    }

    private static IReadOnlyList<AttachedFile> ExtractAttachedFiles(HtmlNode root)
    {
        var anchors = root.SelectNodes(".//a[@href]") ?? Enumerable.Empty<HtmlNode>();
        var files = new List<AttachedFile>();

        foreach (var anchor in anchors)
        {
            var href = anchor.GetAttributeValue("href", string.Empty).Trim();
            var name = NormalizeText(anchor.InnerText);

            if (string.IsNullOrWhiteSpace(href) || string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            if (name == "Tillbaka" || name == "Logga in" || name.Contains("Facebook", StringComparison.OrdinalIgnoreCase) || name == "X")
            {
                continue;
            }

            if (!LooksLikeAttachment(href, name))
            {
                continue;
            }

            var absoluteUrl = Uri.TryCreate(new Uri("https://e-tjanster.are.se"), href, out var resolved)
                ? resolved.AbsoluteUri
                : href;

            files.Add(new AttachedFile(name, absoluteUrl));
        }

        return files
            .DistinctBy(file => file.Url, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static bool LooksLikeAttachment(string href, string name)
    {
        return href.Contains("/file/", StringComparison.OrdinalIgnoreCase)
            || href.Contains("download", StringComparison.OrdinalIgnoreCase)
            || Regex.IsMatch(href, @"\.(pdf|doc|docx|xls|xlsx|ppt|pptx|txt|jpg|jpeg|png|gif|zip)$", RegexOptions.IgnoreCase)
            || Regex.IsMatch(name, @"\.(pdf|doc|docx|xls|xlsx|ppt|pptx|txt|jpg|jpeg|png|gif|zip)$", RegexOptions.IgnoreCase);
    }

    private static int ParseVotes(string votes)
    {
        return int.TryParse(votes, out var result) ? result : 0;
    }

    private static string GetValue(IReadOnlyDictionary<string, string> values, string key)
    {
        return values.TryGetValue(key, out var value) ? value : string.Empty;
    }

    private static string NormalizeText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var decoded = WebUtility.HtmlDecode(HtmlEntity.DeEntitize(value));
        return Regex.Replace(decoded, @"\s+", " ").Trim();
    }

    private static string NormalizeMultilineText(string value)
    {
        var normalized = value.Replace("\r\n", "\n").Replace('\r', '\n');
        normalized = Regex.Replace(normalized, @"[\t\f\v]+", " ");
        normalized = Regex.Replace(normalized, @" *\n *", "\n");
        normalized = Regex.Replace(normalized, @"\n{3,}", "\n\n");
        normalized = Regex.Replace(normalized, @"(?m)^\?\s+", "- ");
        return normalized.Trim();
    }
}

static class TextFileFormatter
{
    public static string Format(SuggestionDetails suggestion)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"Heading: {suggestion.Heading}");
        builder.AppendLine($"ID: {suggestion.Id}");
        builder.AppendLine($"Sent in date: {suggestion.SentInDate}");
        builder.AppendLine($"Vote date: {suggestion.VoteDate}");
        builder.AppendLine($"Votes: {suggestion.Votes}");
        builder.AppendLine($"Status: {suggestion.Status}");
        builder.AppendLine();
        builder.AppendLine("Description:");
        builder.AppendLine(string.IsNullOrWhiteSpace(suggestion.Description) ? "None" : suggestion.Description);
        builder.AppendLine();
        builder.AppendLine("Attached files:");

        if (suggestion.AttachedFiles.Count == 0)
        {
            builder.AppendLine("None");
        }
        else
        {
            foreach (var file in suggestion.AttachedFiles)
            {
                builder.AppendLine($"- {file.Name}: {file.Url}");
            }
        }

        builder.AppendLine();
        builder.AppendLine("Source URL:");
        builder.AppendLine(suggestion.SourceUrl);
        return builder.ToString();
    }
}

sealed record SuggestionSummary(int Id, string Heading, string Status, string VoteDate, int Votes);

sealed record AttachedFile(string Name, string Url);

sealed record SuggestionDetails(
    int Id,
    string Heading,
    string SentInDate,
    string VoteDate,
    int Votes,
    string Status,
    string Description,
    IReadOnlyList<AttachedFile> AttachedFiles,
    string SourceUrl);
