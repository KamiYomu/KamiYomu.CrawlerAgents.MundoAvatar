using HtmlAgilityPack;
using KamiYomu.CrawlerAgents.Core;
using KamiYomu.CrawlerAgents.Core.Catalog;
using KamiYomu.CrawlerAgents.Core.Catalog.Builders;
using KamiYomu.CrawlerAgents.Core.Catalog.Definitions;
using Microsoft.Extensions.Logging;
using PuppeteerSharp;
using System.ComponentModel;
using System.Globalization;
using Page = KamiYomu.CrawlerAgents.Core.Catalog.Page;

namespace KamiYomu.CrawlerAgents.MundoAvatar
{
    [DisplayName("KamiYomu Crawler Agent – mundoavatar.com.br")]
    public class MundoAvatarCrawlerAgent : AbstractCrawlerAgent, ICrawlerAgent
    {
        private bool _disposed = false;
        private readonly Uri _baseUri;
        private readonly string _language;
        private Lazy<Task<IBrowser>> _browser;

        public MundoAvatarCrawlerAgent(IDictionary<string, object> options) : base(options)
        {
            _baseUri = new Uri("https://comics.mundoavatar.com.br");
            _browser = new Lazy<Task<IBrowser>>(CreateBrowserAsync, true);
        }

        public Task<IBrowser> GetBrowserAsync() => _browser.Value;

        private async Task<IBrowser> CreateBrowserAsync()
        {
            var launchOptions = new LaunchOptions
            {
                Headless = true,
                Timeout = TimeoutMilliseconds,
                Args = [
                    "--disable-blink-features=AutomationControlled",
                    "--no-sandbox",
                    "--disable-dev-shm-usage"
                ]
            };

            return await Puppeteer.LaunchAsync(launchOptions);
        }
        private string NormalizeUrl(string url)
        {
            if (string.IsNullOrWhiteSpace(url))
                return string.Empty;

            if (!url.StartsWith("/") && Uri.TryCreate(url, UriKind.Absolute, out var absolute))
                return absolute.ToString();

            var resolved = new Uri(_baseUri, url);
            return resolved.ToString();
        }

        private async Task PreparePageForNavigationAsync(IPage page)
        {
            page.Console += (sender, e) =>
            {
                // e.Message contains the console message
                Logger?.LogDebug($"[Browser Console] {e.Message.Type}: {e.Message.Text}");

                // You can also inspect arguments
                if (e.Message.Args != null)
                {
                    foreach (var arg in e.Message.Args)
                    {
                        Logger?.LogDebug($"   Arg: {arg.RemoteObject.Value}");
                    }
                }
            };

            await page.EvaluateExpressionOnNewDocumentAsync(@"
                // Neutralize devtools detection
                const originalLog = console.log;
                console.log = function(...args) {
                    if (args.length === 1 && args[0] === '[object HTMLDivElement]') {
                        return; // skip detection trick
                    }
                    return originalLog.apply(console, args);
                };

                // Override reload to do nothing
                window.location.reload = () => console.log('Reload prevented');
            ");

            await page.EmulateTimezoneAsync("America/Toronto");

            var fixedDate = DateTime.Now;

            var fixedDateIso = fixedDate.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);

            await page.EvaluateExpressionOnNewDocumentAsync($@"
                // Freeze time to a specific date
                const fixedDate = new Date('{fixedDateIso}');
                Date = class extends Date {{
                    constructor(...args) {{
                        if (args.length === 0) {{
                            return fixedDate;
                        }}
                        return super(...args);
                    }}
                    static now() {{
                        return fixedDate.getTime();
                    }}
                }};
            ");
        }

        public async Task<Manga> GetByIdAsync(string id, CancellationToken cancellationToken)
        {
            var browser = await GetBrowserAsync();
            using var page = await browser.NewPageAsync();
            await PreparePageForNavigationAsync(page);
            await page.SetUserAgentAsync(HttpClientDefaultUserAgent);

            var finalUrl = new Uri(_baseUri, $"series/{id}/").ToString();
            var response = await page.GoToAsync(finalUrl, new NavigationOptions
            {
                WaitUntil = [WaitUntilNavigation.DOMContentLoaded, WaitUntilNavigation.Load],
                Timeout = TimeoutMilliseconds
            });

            foreach (var cookie in await page.GetCookiesAsync())
            {
                Logger?.LogDebug("{name}={value}; Domain={domain}; Path={path}", cookie.Name, cookie.Value, cookie.Domain, cookie.Path);
            }

            var content = await page.GetContentAsync();
            var document = new HtmlDocument();
            document.LoadHtml(content);
            var rootNode = document.DocumentNode.SelectSingleNode("//div[@class='main-info']");
            Manga manga = ConvertToMangaFromSingleBook(rootNode, id);

            return manga;
        }

        private Manga ConvertToMangaFromSingleBook(HtmlNode rootNode, string id)
        {
            // --- Cover image ---
            var imgNode = rootNode.SelectSingleNode(".//div[@class='thumb']//img");
            var coverUrl = imgNode?.GetAttributeValue("src", string.Empty) ?? string.Empty;
            var coverFileName = !string.IsNullOrEmpty(coverUrl)
                ? Path.GetFileName(new Uri(coverUrl).AbsolutePath)
                : string.Empty;

            // --- Title ---
            var titleNode = rootNode.SelectSingleNode(".//h1[@class='entry-title']");
            var title = titleNode?.InnerText.Trim()
                        ?? imgNode?.GetAttributeValue("title", string.Empty)
                        ?? "Unknown Title";

            // --- Alternative titles ---
            var altTitles = new List<string>();
            var altAttr = imgNode?.GetAttributeValue("alt", string.Empty);
            if (!string.IsNullOrWhiteSpace(altAttr)) altTitles.Add(altAttr);
            if (!string.IsNullOrWhiteSpace(title) && !altTitles.Contains(title)) altTitles.Add(title);

            // --- Release status ---
            var statusNode = rootNode.SelectSingleNode(".//div[@class='imptdt'][contains(text(),'Status')]/i");
            var releaseStatus = statusNode?.InnerText.Trim() ?? "Unknown";

            // --- Authors & Artists ---
            var authors = new List<string>();
            var authorNode = rootNode.SelectSingleNode(".//div[@class='imptdt'][contains(text(),'Author')]/i");
            if (authorNode != null) authors.Add(authorNode.InnerText.Trim());

            var artistNode = rootNode.SelectSingleNode(".//div[@class='imptdt'][contains(text(),'Artist')]/i");
            if (artistNode != null) authors.Add(artistNode.InnerText.Trim());

            // --- Genres ---
            var genres = rootNode.SelectNodes(".//span[@class='mgen']/a")
                                 ?.Select(x => x.InnerText.Trim())
                                 .ToList()
                         ?? new List<string>();

            // --- Description ---
            var descNode = rootNode.SelectSingleNode(".//div[@class='entry-content']");
            var description = descNode?.InnerText.Trim()
                              ?? "No Description Available";

            // --- Website URL ---
            var href = imgNode?.GetAttributeValue("src", string.Empty) ?? string.Empty;

            // --- Build Manga object ---
            var manga = MangaBuilder.Create()
                .WithId(id)
                .WithTitle(title)
                .WithAlternativeTitles(
                    altTitles.Select((p, i) => new { i = i.ToString(), p })
                             .ToDictionary(x => x.i, x => x.p))
                .WithDescription(description)
                .WithAuthors(authors.ToArray())
                .WithTags(genres.ToArray())
                .WithCoverUrl(new Uri(coverUrl))
                .WithCoverFileName(coverFileName)
                .WithWebsiteUrl(NormalizeUrl(href))
                .WithIsFamilySafe(true)
                .WithReleaseStatus(releaseStatus.ToLowerInvariant() switch
                {
                    "completed" => ReleaseStatus.Completed,
                    "completo" => ReleaseStatus.Completed,
                    "cancelled" => ReleaseStatus.Cancelled,
                    "cancelado" => ReleaseStatus.Cancelled,
                    _ => ReleaseStatus.Continuing,
                })
                .Build();

            return manga;
        }

        public async Task<IEnumerable<Core.Catalog.Page>> GetChapterPagesAsync(Chapter chapter, CancellationToken cancellationToken)
        {
            var browser = await GetBrowserAsync();
            using var page = await browser.NewPageAsync();

            await PreparePageForNavigationAsync(page);
            await page.SetUserAgentAsync(HttpClientDefaultUserAgent);

            // Now navigate, the site will see the key from the start
            await page.GoToAsync(chapter.Uri.ToString(), new NavigationOptions
            {
                WaitUntil = new[] { WaitUntilNavigation.DOMContentLoaded, WaitUntilNavigation.Load },
                Timeout = TimeoutMilliseconds
            });

            // Wait until all <img> elements inside #readerarea have a src attribute
            await page.EvaluateExpressionAsync(@"
                (function() {
                    const select = document.getElementById('readingmode');
                    if (select) {
                        select.value = 'full';
                        // Trigger the change event so any listeners run
                        const event = new Event('change', { bubbles: true });
                        select.dispatchEvent(event);
                    }
                })();
            ");


            var content = await page.GetContentAsync();
            var document = new HtmlDocument();
            document.LoadHtml(content);

            var pageNodes = document.DocumentNode.SelectNodes("//div[@id='readerarea']//img");

            return ConvertToChapterPages(chapter, pageNodes);
        }

        private IEnumerable<Page> ConvertToChapterPages(Chapter chapter, HtmlNodeCollection pageNodes)
        {
            if (pageNodes == null)
                return Enumerable.Empty<Page>();

            var pages = new List<Page>();

            foreach (var imgNode in pageNodes)
            {
                // Ensure it's an <img>
                if (imgNode.Name != "img") continue;

                // Get page number from data-index
                var indexAttr = imgNode.GetAttributeValue("data-index", null);
                var pageNumber = 0m;
                if (!string.IsNullOrEmpty(indexAttr) && decimal.TryParse(indexAttr, out var parsedIndex))
                    pageNumber = parsedIndex;

                // Image URL
                var imageUrl = imgNode.GetAttributeValue("src", null);
                if (string.IsNullOrEmpty(imageUrl)) continue;

                // Build a unique ID (chapterId + pageNumber)
                var pageId = $"{chapter.Id}-p{pageNumber}";

                // Build Page object
                var page = PageBuilder.Create()
                    .WithChapterId(chapter.Id)
                    .WithId(pageId)
                    .WithPageNumber(pageNumber > 0 ? pageNumber : 0)
                    .WithImageUrl(new Uri(imageUrl))
                    .WithParentChapter(chapter)
                    .Build();

                pages.Add(page);
            }

            return pages;
        }

        public async Task<PagedResult<Chapter>> GetChaptersAsync(Manga manga, PaginationOptions paginationOptions, CancellationToken cancellationToken)
        {
            var browser = await GetBrowserAsync();
            using var page = await browser.NewPageAsync();
            await PreparePageForNavigationAsync(page);
            await page.SetUserAgentAsync(HttpClientDefaultUserAgent);

            var finalUrl = new Uri(_baseUri, $"series/{manga.Id}/").ToString();
            var response = await page.GoToAsync(finalUrl, new NavigationOptions
            {
                WaitUntil = [WaitUntilNavigation.DOMContentLoaded, WaitUntilNavigation.Load],
                Timeout = TimeoutMilliseconds
            });

            foreach (var cookie in await page.GetCookiesAsync())
            {
                Logger?.LogDebug("{name}={value}; Domain={domain}; Path={path}", cookie.Name, cookie.Value, cookie.Domain, cookie.Path);
            }

            var content = await page.GetContentAsync();

            var document = new HtmlDocument();
            document.LoadHtml(content);
            var rootNode = document.DocumentNode.SelectSingleNode("//div[@class='eplister']");
            IEnumerable<Chapter> chapters = ConvertChaptersFromSingleBook(manga, rootNode);

            return PagedResultBuilder<Chapter>.Create()
                                              .WithPaginationOptions(new PaginationOptions(chapters.Count(), chapters.Count(), chapters.Count()))
                                              .WithData(chapters)
                                              .Build();
        }

        private List<Chapter> ConvertChaptersFromSingleBook(Manga manga, HtmlNode rootNode)
        {
            var chapters = new List<Chapter>();

            // Select all <li> elements inside the chapter list
            var chapterDivs = rootNode.SelectNodes(".//ul[@class='clstyle']/li");
            if (chapterDivs == null) return chapters;

            foreach (var chapterDiv in chapterDivs)
            {
                // --- Extract chapter number ---
                var numberAttr = chapterDiv.GetAttributeValue("data-num", "0");
                var number = int.TryParse(numberAttr, out var numResult) ? numResult : 0;

                // --- Extract anchor and URI ---
                var anchorNode = chapterDiv.SelectSingleNode(".//a");
                var uri = NormalizeUrl(anchorNode?.GetAttributeValue("href", string.Empty)) ?? string.Empty;

                // --- Extract title ---
                var titleNode = chapterDiv.SelectSingleNode(".//span[@class='chapternum']");
                var title = titleNode?.InnerText.Trim() ?? $"Chapter {number}";

                // --- Extract volume (not present, default to 0) ---
                var volume = 0;

                // --- Build chapter ID (unique per manga + number) ---
                var chapterId = $"{manga.Id}-ch{number}";

                // --- Language (site is Brazilian Portuguese) ---
                var _language = "Portuguese";

                // --- Build Chapter object ---
                var chapter = ChapterBuilder.Create()
                    .WithId(chapterId)
                    .WithTitle(title)
                    .WithParentManga(manga)
                    .WithVolume(volume > 0 ? volume : 0)
                    .WithNumber(number > 0 ? number : 0)
                    .WithUri(new Uri(uri))
                    .WithTranslatedLanguage(_language)
                    .Build();

                chapters.Add(chapter);
            }

            return chapters;
        }

        public Task<Uri> GetFaviconAsync(CancellationToken cancellationToken)
        {
            return Task.FromResult(new Uri("https://comics.mundoavatar.com.br/wp-content/uploads/2024/03/cropped-cropped-favicon-1-32x32.png"));
        }

        public async Task<PagedResult<Manga>> SearchAsync(string titleName, PaginationOptions paginationOptions, CancellationToken cancellationToken)
        {
            var browser = await GetBrowserAsync();
            using var page = await browser.NewPageAsync();
            await PreparePageForNavigationAsync(page);
            await page.SetUserAgentAsync(HttpClientDefaultUserAgent);
            var pageNumber = string.IsNullOrWhiteSpace(paginationOptions?.ContinuationToken)
                            ? 1
                            : int.Parse(paginationOptions.ContinuationToken);

            var targetUri = new Uri(new Uri(_baseUri.ToString()), $"series/?type=comic&s={titleName}&page={pageNumber}");
            await page.GoToAsync(targetUri.ToString(), new NavigationOptions
            {
                WaitUntil = [WaitUntilNavigation.DOMContentLoaded, WaitUntilNavigation.Load],
                Timeout = TimeoutMilliseconds
            });

            foreach (var cookie in await page.GetCookiesAsync())
            {
                Logger?.LogDebug("{name}={value}; Domain={domain}; Path={path}", cookie.Name, cookie.Value, cookie.Domain, cookie.Path);
            }

            var content = await page.GetContentAsync();

            var document = new HtmlDocument();
            document.LoadHtml(content);

            List<Manga> mangas = [];
            if (pageNumber > 0)
            {
                var nodes = document.DocumentNode.SelectNodes("//div[contains(@class, 'listupd')]");

                if (nodes != null)
                {
                    foreach (var divNode in nodes)
                    {
                        Manga manga = ConvertToMangaFromList(divNode);
                        mangas.Add(manga);
                    }
                }
            }

            return PagedResultBuilder<Manga>.Create()
                .WithData(mangas)
                .WithPaginationOptions(new PaginationOptions((pageNumber + 1).ToString()))
                .Build();
        }

        private Manga ConvertToMangaFromList(HtmlNode divNode)
        {
            // Anchor node (contains href, title, and inner structure)
            var anchorNode = divNode.SelectSingleNode(".//a");
            var websiteUrl = anchorNode?.GetAttributeValue("href", string.Empty) ?? string.Empty;

            // Title (from <div class="tt"> or anchor title attribute)
            var titleNode = divNode.SelectSingleNode(".//div[@class='tt']");
            var title = titleNode?.InnerText.Trim()
                        ?? anchorNode?.GetAttributeValue("title", string.Empty)
                        ?? "Unknown Title";

            // Alternative titles (use alt attribute of <img> or anchor title)
            var imgNode = divNode.SelectSingleNode(".//img");
            var altTitle = imgNode?.GetAttributeValue("alt", string.Empty);
            var altTitles = new List<string>();
            if (!string.IsNullOrWhiteSpace(altTitle)) altTitles.Add(altTitle);
            if (!string.IsNullOrWhiteSpace(title) && !altTitles.Contains(title)) altTitles.Add(title);

            // Cover image
            var coverUrl = imgNode?.GetAttributeValue("src", string.Empty) ?? string.Empty;
            var coverFileName = Path.GetFileName(new Uri(coverUrl).AbsolutePath);

            // Status (Completed, Ongoing, etc.)
            var statusNode = divNode.SelectSingleNode(".//span[contains(@class,'status')]");
            var status = statusNode?.InnerText.Trim() ?? "Unknown";

            // Latest chapter (e.g., "Chapter 5" or "Chapter ?")
            var chapterNode = divNode.SelectSingleNode(".//div[@class='epxs']");
            var chapterText = chapterNode?.InnerText.Replace("Chapter", string.Empty).Trim() ?? "0";
            var chapter = chapterText;

            // Volume info (not present in snippet, default to "0")
            var volume = "0";

            // Author (not present in snippet, default placeholder)
            var author = "Unknown Author";

            // Genres (not present in snippet, default empty)
            var genres = new List<string>();

            // Generate a unique ID (e.g., hash of URL or title)
            var uri = new Uri(websiteUrl);
            var path = uri.AbsolutePath.TrimEnd('/');
            var id = path.Split('/').Last();


            // --- Build Manga ---
            var manga = MangaBuilder.Create()
                .WithId(id)
                .WithTitle(title)
                .WithAuthors(new[] { author })
                .WithDescription("No Description Available")
                .WithCoverUrl(new Uri(coverUrl))
                .WithCoverFileName(coverFileName)
                .WithWebsiteUrl(websiteUrl)
                .WithAlternativeTitles(
                    altTitles.Select((p, i) => new { i = i.ToString(), p })
                             .ToDictionary(x => x.i, x => x.p))
                .WithLatestChapterAvailable(decimal.TryParse(chapter, out var chapterResult) ? chapterResult : 0)
                .WithLastVolumeAvailable(decimal.TryParse(volume, out var volumeResult) ? volumeResult : 0)
                .WithOriginalLanguage("pt-BR")
                .WithIsFamilySafe(true)
                .WithReleaseStatus(status.ToLower() switch
                {
                    "completed" => ReleaseStatus.Completed,
                    "completo" => ReleaseStatus.Completed,
                    "cancelled" => ReleaseStatus.Cancelled,
                    "cancelado" => ReleaseStatus.Cancelled,
                    _ => ReleaseStatus.Continuing,
                })
                .Build();

            return manga;
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                if (disposing)
                {
                    if (_browser.IsValueCreated)
                    {
                        _browser.Value.Result.Dispose();
                    }

                }

                _disposed = true;
            }
        }

        // TODO: override finalizer only if 'Dispose(bool disposing)' has code to free unmanaged resources
        ~MundoAvatarCrawlerAgent()
        {
            // Do not change this code. Put cleanup code in 'Dispose(bool disposing)' method
            Dispose(disposing: false);
        }

        public void Dispose()
        {
            // Do not change this code. Put cleanup code in 'Dispose(bool disposing)' method
            Dispose(disposing: true);
            GC.SuppressFinalize(this);
        }
    }
}
