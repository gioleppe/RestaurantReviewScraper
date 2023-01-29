using PuppeteerSharp;

namespace RestaurantReviewScraper;

internal static class ElementExtensions
{
    internal static async Task<string> GetInnerTextAsync(this IElementHandle elementHandle)
    {
        return await elementHandle.EvaluateFunctionAsync<string>("e => e.innerText");
    }

    internal static async Task<string> GetReviewText(this IElementHandle elementHandle)
    {
        var firstPart = await elementHandle.GetInnerTextAsync();
        var secondPart = await elementHandle.QuerySelectorAsync(".postSnippet");
        // if there's a post snippet we need to merge the review with the hidden text
        return secondPart is not null
            ? string.Join(" ", firstPart[..firstPart.LastIndexOf("...", StringComparison.Ordinal)],
                await secondPart.GetInnerTextAsync())
            : firstPart;
    }

    internal static async Task<string> GetReviewDate(this IElementHandle elementHandle)
    {
        var titleProperty = await elementHandle.GetPropertyAsync("title");
        return titleProperty.ToString()!.Replace("JSHandle:", "");
    }

    internal static async Task<int> GetReviewRating(this IElementHandle elementHandle)
    {
        var titlePropertyHandle = await elementHandle.GetPropertyAsync("className");
        var titleProperty = titlePropertyHandle.ToString();
        var ratingString = (titleProperty![titleProperty!.LastIndexOf(" ", StringComparison.Ordinal)..]);
        var rating = int.Parse(ratingString[^2..]);
        return rating;
    }
}