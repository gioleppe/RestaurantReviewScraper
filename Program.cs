using CsvHelper;
using Newtonsoft.Json;
using Polly;
using PuppeteerSharp;

namespace RestaurantReviewScraper;

internal static class Program
{
    private static readonly Random Rand = new(42);

    public static async Task Main(string[] args)
    {
        if (args.Length != 1)
        {
            Console.WriteLine("usage: $rescrap <restaurants.csv>");
            //Environment.Exit(1);
        }

        using var streamReader =
            new StreamReader(
                args[0]);
        using var csv = new CsvReader(streamReader, System.Globalization.CultureInfo.InvariantCulture);
        var restaurantsCsv = csv.GetRecords<RestaurantCSV>().ToList();
        var restaurants = restaurantsCsv.Select(res => new Restaurant(res.Name, res.Ranking, res.Url)).ToList();

        Console.WriteLine($"Scraping reviews for {restaurants.Count} restaurants");

        var browserPage = await Setup();


        Dictionary<Restaurant, List<Review>> allReviews = new();

        var retryPolicy = Policy
            .Handle<Exception>()
            .WaitAndRetryAsync(5, retryAttempt => TimeSpan.FromSeconds(Math.Pow(3, retryAttempt)),
                (exception, timeSpan, retryCount, context) =>
                {
                    Console.WriteLine(exception);
                    if (retryCount == 5)
                    {
                        SerializeReviews(allReviews, args[1]);
                        Environment.Exit(1);
                    }
                });

        await retryPolicy.ExecuteAsync(() => ScrapingLoop(restaurants, browserPage, allReviews));

        Console.WriteLine("Scraping is over :)");
        SerializeReviews(allReviews, args[1]);
    }

    private static async Task ScrapingLoop(ICollection<Restaurant> restaurants, IPage browserPage,
        IDictionary<Restaurant, List<Review>> allReviews)
    {
        foreach (var restaurant in restaurants.ToList())
        {
            Console.WriteLine($"Scraping restaurant {restaurant.Name} ranked: {restaurant.Ranking}");
            await browserPage.GoToAsync(restaurant.Url, WaitUntilNavigation.Networkidle2);
            await ClickCookiePolicyIfVisible(browserPage);
            await GetRestaurantAddress(restaurant, browserPage);
            if (!await WaitAndClickLanguagesFilter(browserPage)) continue;
            var reviews = await ExtractReviews(browserPage);
            allReviews.Add(restaurant, reviews);
            restaurants.Remove(restaurant);
        }
    }

    private static void SerializeReviews(Dictionary<Restaurant, List<Review>> reviewDictionary, string path)
    {
        var serializer = new JsonSerializer();
        using var streamWriter =
            new StreamWriter(
                path);
        using var jsonTextWriter = new JsonTextWriter(streamWriter);
        serializer.Serialize(jsonTextWriter, reviewDictionary.Select(pair =>
        {
            pair.Key.Deconstruct(out var name, out var ranking, out var url, out var address);
            return new RestaurantWithReviews(name, ranking, url, address!, pair.Value);
        }));
    }


    private static async Task GetRestaurantAddress(Restaurant restaurant, IPage browserPage)
    {
        var addressElement = await browserPage.QuerySelectorAsync("span.yEWoV:nth-child(1)");
        restaurant.Address = await addressElement.GetInnerTextAsync();
    }

    private static async Task ClickCookiePolicyIfVisible(IPage page)
    {
        try
        {
            var policy = await page.WaitForSelectorAsync("#onetrust-accept-btn-handler", new WaitForSelectorOptions
            {
                Timeout = 300,
                Visible = true,
            });
            await policy.ClickAsync();
            await page.WaitForTimeoutAsync(500);
        }
        catch (Exception e)
        {
            Console.WriteLine("Cookie Policy already accepted");
        }
    }

    private static async Task<List<Review>> ExtractReviews(IPage page)
    {
        List<Review> reviews = new();

        var pageCount = 1;
        do
        {
            var reviewsDivs = await page.QuerySelectorAllAsync(".review-container");

            var showMoreButton = await page.QuerySelectorAsync(".partial_entry > span");
            if (showMoreButton is not null)
            {
                await page.EvaluateExpressionAsync(
                    "document.querySelector(\".review-container .partial_entry > span\").click();");
                await page.WaitForTimeoutAsync(Rand.Next(500, 1500));
            }

            foreach (var div in reviewsDivs)
            {
                var reviewTitle = await div.QuerySelectorAsync(".quote");
                var reviewDate = await div.QuerySelectorAsync(".ratingDate");
                var reviewText = await div.QuerySelectorAsync(".partial_entry");
                var reviewRating = await div.QuerySelectorAsync(".ui_bubble_rating");

                reviews.Add(new Review(
                    await reviewTitle.GetInnerTextAsync(),
                    await reviewDate.GetReviewDate(),
                    await reviewText.GetReviewText(),
                    await reviewRating.GetReviewRating()));
            }

            Console.WriteLine($"scraping review page: {pageCount}");
            pageCount += 1;
            
        } while (await GoToNextReviewPage(page));

        return reviews;
    }

    private static async Task<bool> GoToNextReviewPage(IPage browserPage)
    {
        try
        {
            var nextButton =
                await browserPage.WaitForSelectorAsync(
                    ".prw_common_responsive_pagination > div:nth-child(1) > a:nth-child(2)");

            // get properties to see if it's disabled
            var classname = await nextButton.GetPropertyAsync("className");
            if (classname.ToString()!.Contains("disabled"))
            {
                return false;
            }

            await nextButton.ClickAsync();

            const string loaderSelector = ".ppr_priv_hotels_loading_box";

            await browserPage.WaitForSelectorAsync(loaderSelector, options: new WaitForSelectorOptions
            {
                Hidden = true
            });

            await browserPage.WaitForTimeoutAsync(Rand.Next(500, 2000));

            return true;
        }
        catch (Exception e)
        {
            return false;
        }
    }

    private static async Task<bool> WaitAndClickLanguagesFilter(IPage page)
    {
        const string allLanguagesSelector = "[for=\"filters_detail_language_filterLang_ALL\"]";
        const string loaderSelector = ".ppr_priv_hotels_loading_box";

        Console.WriteLine("Waiting for language selector");
        try
        {
            var element = await page.WaitForSelectorAsync(allLanguagesSelector, new WaitForSelectorOptions
            {
                Timeout = 1000,
            });
            await element.ClickAsync();

            await page.WaitForTimeoutAsync(1000);

            await page.WaitForSelectorAsync(loaderSelector, options: new WaitForSelectorOptions
            {
                Hidden = true
            });
            return true;
        }
        catch (WaitTaskTimeoutException)
        {
            Console.WriteLine("No language button found! The restaurant has no reviews, skipping!");
            return false;
        }
    }

    private static async Task<IPage> Setup()
    {
        var options = new LaunchOptions()
        {
            Headless = true,
            ExecutablePath = "C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe"
        };

        var browser = await Puppeteer.LaunchAsync(options);
        var browserPage = await browser.NewPageAsync();
        var headers = new Dictionary<string, string> { { "Referer", "https://www.google.com/" } };
        await browserPage.SetExtraHttpHeadersAsync(headers);
        return browserPage;
    }
}

internal sealed record Review(string title, string date, string text, int rating);

// ReSharper disable once ClassNeverInstantiated.Global
internal sealed class Restaurant
{
    public Restaurant(string Name, string Ranking, string Url)
    {
        this.Name = Name;
        this.Ranking = Ranking;
        this.Url = Url;
    }

    public string Name { get; }
    public string Ranking { get; }
    public string Url { get; }

    public string? Address { get; set; }

    public void Deconstruct(out string Name, out string Ranking, out string Url, out string? Address)
    {
        Name = this.Name;
        Ranking = this.Ranking;
        Url = this.Url;
        Address = this.Address;
    }
}

internal sealed record RestaurantCSV(string Name, string Ranking, string Url);

internal sealed record RestaurantWithReviews(string Name, string Ranking, string Url, string Address,
    List<Review> Reviews);