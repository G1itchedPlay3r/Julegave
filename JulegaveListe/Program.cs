using System.Globalization;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;
using OpenQA.Selenium;
using OpenQA.Selenium.Chrome;
using OpenQA.Selenium.Support.UI;

// Windows API for hiding console window
static class NativeMethods
{
    [DllImport("kernel32.dll")]
    public static extern IntPtr GetConsoleWindow();

    [DllImport("user32.dll")]
    public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
}

// Browser automation service for sites that block scraping or use JavaScript
class BrowserAutomation
{
    private static ChromeDriver? _driver;
    private static readonly object _lock = new object();

    public static ChromeDriver GetDriver()
    {
        lock (_lock)
        {
            if (_driver == null)
            {
                try
                {
                    var options = new ChromeOptions();
                    
                    // Try to find Chrome/Chromium binary on Linux
                    if (OperatingSystem.IsLinux())
                    {
                        string[] possiblePaths = {
                            "/usr/bin/chromium-browser",
                            "/usr/bin/chromium",
                            "/usr/bin/google-chrome",
                            "/usr/bin/google-chrome-stable",
                            "/snap/bin/chromium",
                            "/usr/bin/chrome"
                        };
                        
                        string? chromePath = possiblePaths.FirstOrDefault(File.Exists);
                        if (chromePath != null)
                        {
                            Console.WriteLine($"Found Chrome at: {chromePath}");
                            options.BinaryLocation = chromePath;
                        }
                        else
                        {
                            Console.WriteLine("Chrome binary not found in standard locations. Checking PATH...");
                            // Try to find it via 'which' command
                            try
                            {
                                var psi = new System.Diagnostics.ProcessStartInfo
                                {
                                    FileName = "/bin/sh",
                                    Arguments = "-c \"which chromium-browser || which chromium || which google-chrome\"",
                                    RedirectStandardOutput = true,
                                    UseShellExecute = false
                                };
                                using var proc = System.Diagnostics.Process.Start(psi);
                                if (proc != null)
                                {
                                    string output = proc.StandardOutput.ReadToEnd().Trim();
                                    proc.WaitForExit();
                                    if (!string.IsNullOrEmpty(output) && File.Exists(output))
                                    {
                                        Console.WriteLine($"Found Chrome via which: {output}");
                                        options.BinaryLocation = output;
                                    }
                                }
                            }
                            catch { }
                        }
                    }
                    
                    options.AddArgument("--headless=new");
                    options.AddArgument("--disable-gpu");
                    options.AddArgument("--no-sandbox");
                    options.AddArgument("--disable-dev-shm-usage");
                    options.AddArgument("--disable-blink-features=AutomationControlled");
                    options.AddArgument("--log-level=3");
                    options.AddArgument("--disable-images");
                    options.AddArgument("--disable-extensions");
                    options.AddArgument("--disable-plugins");
                    options.AddArgument("--blink-settings=imagesEnabled=false");
                    options.AddArgument("--disable-software-rasterizer");
                    options.AddArgument("--disable-webgl");
                    options.AddArgument("--user-agent=Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36");
                    options.AddArgument("--lang=da-DK");
                    options.AddExcludedArgument("enable-automation");
                    options.AddAdditionalOption("useAutomationExtension", false);
                    
                    // Set preferences to disable images and speed up loading
                    options.AddUserProfilePreference("profile.default_content_setting_values.images", 2);
                    options.AddUserProfilePreference("profile.managed_default_content_settings.images", 2);
                    
                    // Create ChromeDriverService with explicit paths for Linux
                    ChromeDriverService? service = null;
                    if (OperatingSystem.IsLinux())
                    {
                        string[] driverPaths = {
                            "/usr/bin/chromedriver",
                            "/usr/lib/chromium-browser/chromedriver",
                            "/snap/bin/chromium.chromedriver"
                        };
                        
                        string? driverPath = driverPaths.FirstOrDefault(File.Exists);
                        if (driverPath != null)
                        {
                            Console.WriteLine($"Found ChromeDriver at: {driverPath}");
                            var driverDir = Path.GetDirectoryName(driverPath) ?? "/usr/bin";
                            service = ChromeDriverService.CreateDefaultService(driverDir, Path.GetFileName(driverPath));
                            service.SuppressInitialDiagnosticInformation = true;
                        }
                    }
                    
                    if (service != null)
                    {
                        _driver = new ChromeDriver(service, options);
                    }
                    else
                    {
                        _driver = new ChromeDriver(options);
                    }
                    
                    _driver.Manage().Timeouts().ImplicitWait = TimeSpan.FromSeconds(3);
                    _driver.Manage().Timeouts().PageLoad = TimeSpan.FromSeconds(15);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"\n❌ Failed to start Chrome browser:");
                    Console.Error.WriteLine($"   {ex.Message}");
                    Console.Error.WriteLine("\nChrome/Chromium is installed but ChromeDriver may be missing.");
                    Console.Error.WriteLine("\nTo install ChromeDriver on Linux:");
                    Console.Error.WriteLine("  Ubuntu/Debian: sudo apt-get install chromium-chromedriver");
                    Console.Error.WriteLine("  Or download from: https://chromedriver.chromium.org/downloads");
                    Console.Error.WriteLine("\nAlternatively, you can enter prices manually when adding items.\n");
                    throw;
                }
            }
            return _driver;
        }
    }

    public static void Quit()
    {
        lock (_lock)
        {
            _driver?.Quit();
            _driver?.Dispose();
            _driver = null;
        }
    }

    public static async Task<(string productName, float price, bool isManualPrice)> GetProductInfoWithBrowser(string url)
    {
        var driver = GetDriver();
        
        try
        {
            Console.WriteLine($"Loading page with browser...");
            driver.Navigate().GoToUrl(url);
            
            // Wait for page to fully load (including JavaScript)
            await Task.Delay(3000);

            // Try to find product name - prioritize common e-commerce patterns
            string productName = "Ukendt produkt";
            string[] nameSelectors = {
                "[product-display-name]",  // Proshop
                "h1[itemprop='name']",  // Schema.org
                "[itemprop='name']",  // Schema.org any element
                "h1.product-name",
                "h1.product-title",
                ".product-name h1",
                ".product-title h1",
                "h1",  // Fallback to any h1
            };

            foreach (var selector in nameSelectors)
            {
                try
                {
                    var element = driver.FindElement(By.CssSelector(selector));
                    
                    // Try attribute first (Proshop uses this)
                    string? attrName = element.GetAttribute("product-display-name");
                    if (!string.IsNullOrWhiteSpace(attrName))
                    {
                        productName = attrName;
                        Console.WriteLine($"✓ Found name: {productName}");
                        break;
                    }
                    
                    // Try text content
                    string? text = element.Text?.Trim();
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        productName = text;
                        Console.WriteLine($"✓ Found name: {productName}");
                        break;
                    }
                }
                catch { continue; }
            }

            // Try to find price - prioritize visible price elements
            float price = 0f;
            string[] priceSelectors = {
                ".site-currency-attention",  // Proshop (both campaign and regular)
                "[itemprop='price']",  // Schema.org
                ".price-now",
                ".current-price", 
                ".sale-price",
                ".product-price span",
                ".product-price",
                ".price span",
                ".price",
                "[data-price]",  // Data attribute
            };

            foreach (var selector in priceSelectors)
            {
                try
                {
                    var element = driver.FindElement(By.CssSelector(selector));
                    
                    // Try content/data attributes first
                    string? attrPrice = element.GetAttribute("content") ?? element.GetAttribute("data-price");
                    if (!string.IsNullOrWhiteSpace(attrPrice) && float.TryParse(attrPrice, NumberStyles.Any, CultureInfo.InvariantCulture, out float attrPriceValue))
                    {
                        price = attrPriceValue;
                        Console.WriteLine($"✓ Found price: {price} kr");
                        break;
                    }
                    
                    // Try visible text
                    string? text = element.Text?.Trim();
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        // Extract numbers from text (handles formats like "1.299,00 kr" or "1299 kr" or "299.95 kr")
                        var match = Regex.Match(text, @"(\d{1,3}(?:[.,\s]\d{3})*(?:[.,]\d{2})?)");
                        if (match.Success)
                        {
                            string priceText = match.Groups[1].Value;
                            // Normalize: ignore decimals (after comma or dot)
                            // Split on comma or dot and only keep the part before it
                            if (priceText.Contains(","))
                            {
                                priceText = priceText.Split(',')[0];
                            }
                            if (priceText.Contains("."))
                            {
                                priceText = priceText.Split('.')[0];
                            }
                            priceText = priceText.Replace(" ", "");
                            
                            if (float.TryParse(priceText, NumberStyles.Any, CultureInfo.InvariantCulture, out float parsedPrice))
                            {
                                price = parsedPrice;
                                Console.WriteLine($"✓ Found price: {price} kr");
                                break;
                            }
                        }
                    }
                }
                catch { continue; }
            }

            bool isManualPrice = false;
            if (price == 0f)
            {
                Console.WriteLine("⚠️  Could not find price on page");
                isManualPrice = true;
            }
            if (productName == "Ukendt produkt")
            {
                Console.WriteLine("⚠️  Could not find product name on page");
            }

            return (productName, price, isManualPrice);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ Browser error: {ex.Message}");
            throw;
        }
    }
}
class ProductInformation 
{
    private static readonly HttpClient client = CreateClient();

    private static HttpClient CreateClient()
    {
        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = true,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
        };
        var c = new HttpClient(handler);
        c.Timeout = TimeSpan.FromSeconds(30);
        c.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36");
        c.DefaultRequestHeaders.Accept.ParseAdd("text/html,application/xhtml+xml,application/xml;q=0.9,image/avif,image/webp,image/apng,*/*;q=0.8,application/signed-exchange;v=b3;q=0.7");
        c.DefaultRequestHeaders.AcceptLanguage.ParseAdd("da-DK,da;q=0.9,en-US;q=0.8,en;q=0.7");
        c.DefaultRequestHeaders.AcceptEncoding.ParseAdd("gzip, deflate, br");
        c.DefaultRequestHeaders.Add("DNT", "1");
        c.DefaultRequestHeaders.Add("sec-ch-ua", "\"Google Chrome\";v=\"131\", \"Chromium\";v=\"131\", \"Not_A Brand\";v=\"24\"");
        c.DefaultRequestHeaders.Add("sec-ch-ua-mobile", "?0");
        c.DefaultRequestHeaders.Add("sec-ch-ua-platform", "\"Windows\"");
        c.DefaultRequestHeaders.Add("sec-fetch-dest", "document");
        c.DefaultRequestHeaders.Add("sec-fetch-mode", "navigate");
        c.DefaultRequestHeaders.Add("sec-fetch-site", "none");
        c.DefaultRequestHeaders.Add("sec-fetch-user", "?1");
        c.DefaultRequestHeaders.Add("upgrade-insecure-requests", "1");
        c.DefaultRequestHeaders.CacheControl = new System.Net.Http.Headers.CacheControlHeaderValue { NoCache = true };
        return c;
    }

    public static async Task<(string productName, float price, bool isManualPrice)> GetProductInfoAsync(string url)
    {
        // Use browser automation for ALL websites - most reliable method
        Console.WriteLine($"Fetching product from: {url}");
        return await BrowserAutomation.GetProductInfoWithBrowser(url);
    }
}
class FileStorage
{
    private readonly string _originalFilePath;
    private string _effectiveFilePath;

    public FileStorage(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath)) throw new ArgumentNullException(nameof(filePath));
        _originalFilePath = Path.GetFullPath(filePath);
        _effectiveFilePath = _originalFilePath;
    }

    // effective path used for actual IO; may be switched to a fallback if original is not writable
    string FilePath => _effectiveFilePath;

    // Format per line: <escaped product>|<price-in-invariant-culture>|<escaped url>|<priceRunnerProductId>|<lastPriceUpdate>|<shopName>
    private static string Escape(string s) => s?.Replace("\\", "\\\\").Replace("|", "\\|") ?? string.Empty;
    private static string Unescape(string s) => s?.Replace("\\|", "|").Replace("\\\\", "\\") ?? string.Empty;

    // Splits a line by '|' honoring backslash-escaping (\"|\" or \"\\\\\")
    private static string[] SplitEscaped(string line)
    {
        var parts = new List<string>();
        if (string.IsNullOrEmpty(line)) return parts.ToArray();

        var sb = new StringBuilder();
        bool escape = false;
        foreach (char c in line)
        {
            if (escape)
            {
                // accept any escaped char
                sb.Append(c);
                escape = false;
                continue;
            }

            if (c == '\\')
            {
                escape = true;
                continue;
            }

            if (c == '|')
            {
                parts.Add(sb.ToString());
                sb.Clear();
                continue;
            }

            sb.Append(c);
        }

        parts.Add(sb.ToString());
        return parts.ToArray();
    }

    // Build a per-user fallback path in LocalApplicationData
    private string GetFallbackPath()
    {
        string userDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string dir = Path.Combine(userDir, "JulegaveListe");
        string fileName = Path.GetFileName(_originalFilePath);
        if (string.IsNullOrEmpty(fileName))
        {
            fileName = "gifts.txt";
        }
        return Path.Combine(dir, fileName);
    }

    public async Task<List<GiftInfo>> LoadAsync()
    {
        var list = new List<GiftInfo>();
        string pathToRead = FilePath;

        // Check if primary path exists, otherwise use fallback
        if (!File.Exists(FilePath))
        {
            string fallback = GetFallbackPath();
            if (File.Exists(fallback))
            {
                pathToRead = fallback;
            }
        }

        try
        {
            if (File.Exists(pathToRead))
            {
                using var fs = new FileStream(pathToRead, FileMode.Open, FileAccess.Read, FileShare.Read);
                using var sr = new StreamReader(fs, Encoding.UTF8);
                string? line;
                while ((line = await sr.ReadLineAsync()) != null)
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;

                    var parts = SplitEscaped(line);
                    if (parts.Length < 2) continue;

                    string product = Unescape(parts[0]);
                    string pricePart = parts.Length >= 2 ? parts[1] : "0";
                    string urlPart = parts.Length >= 3 ? Unescape(parts[2]) : string.Empty;
                    string priceRunnerIdPart = parts.Length >= 4 ? Unescape(parts[3]) : string.Empty;
                    string lastUpdatePart = parts.Length >= 5 ? parts[4] : string.Empty;
                    string shopNamePart = parts.Length >= 6 ? Unescape(parts[5]) : string.Empty;

                    if (!float.TryParse(pricePart, NumberStyles.Any, CultureInfo.InvariantCulture, out float price))
                    {
                        // try fallback parse
                        float.TryParse(Regex.Replace(pricePart, @"[^\d.]", ""), NumberStyles.Any, CultureInfo.InvariantCulture, out price);
                    }

                    DateTime lastUpdate = DateTime.MinValue;
                    if (!string.IsNullOrEmpty(lastUpdatePart))
                    {
                        DateTime.TryParse(lastUpdatePart, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out lastUpdate);
                    }

                    list.Add(new GiftInfo(product, price, urlPart, priceRunnerIdPart, lastUpdate, shopNamePart));
                }
            }
        }
        catch (Exception)
        {
            // Silent fail - return empty list
        }

        return list;
    }

    public async Task AppendAsync(GiftInfo gift)
    {
        string line = $"{Escape(gift.Produkt)}|{gift.Price.ToString(CultureInfo.InvariantCulture)}|{Escape(gift.URl)}|{Escape(gift.PriceRunnerProductId)}|{gift.LastPriceUpdate.ToString("o", CultureInfo.InvariantCulture)}|{Escape(gift.ShopName)}|{gift.IsManualPrice}{Environment.NewLine}";

        bool primarySuccess = false;
        
        // Try to write to primary location (project folder)
        try
        {
            var dir = Path.GetDirectoryName(FilePath) ?? AppContext.BaseDirectory;
            Directory.CreateDirectory(dir);
            await File.AppendAllTextAsync(FilePath, line, Encoding.UTF8);
            primarySuccess = true;
        }
        catch (UnauthorizedAccessException)
        {
        }
        catch (DirectoryNotFoundException)
        {
        }
        catch (Exception)
        {
        }

        // Also write to AppData fallback location (for backup)
        try
        {
            string fallbackPath = GetFallbackPath();
            var fallbackDir = Path.GetDirectoryName(fallbackPath) ?? Path.GetDirectoryName(_originalFilePath) ?? AppContext.BaseDirectory;
            Directory.CreateDirectory(fallbackDir);
            await File.AppendAllTextAsync(fallbackPath, line, Encoding.UTF8);
            
            // If primary write failed, copy entire fallback file back to primary location
            if (!primarySuccess)
            {
                try
                {
                    var dir = Path.GetDirectoryName(FilePath) ?? AppContext.BaseDirectory;
                    Directory.CreateDirectory(dir);
                    File.Copy(fallbackPath, FilePath, overwrite: true);
                }
                catch (Exception)
                {
                }
            }
        }
        catch (Exception)
        {
            if (!primarySuccess)
            {
                throw; // If both failed, throw the exception
            }
        }
    }

    // save/overwrite the file with the provided list so data persists after closing terminal
    public async Task SaveAsync(IEnumerable<GiftInfo> gifts)
    {
        var sb = new StringBuilder();
        foreach (var g in gifts)
        {
            sb.Append(Escape(g.Produkt));
            sb.Append('|');
            sb.Append(g.Price.ToString(CultureInfo.InvariantCulture));
            sb.Append('|');
            sb.Append(Escape(g.URl ?? string.Empty));
            sb.Append('|');
            sb.Append(Escape(g.PriceRunnerProductId ?? string.Empty));
            sb.Append('|');
            sb.Append(g.LastPriceUpdate.ToString("o", CultureInfo.InvariantCulture));
            sb.Append('|');
            sb.Append(Escape(g.ShopName ?? string.Empty));
            sb.AppendLine();
        }

        bool primarySuccess = false;

        // Try to write to primary location (project folder)
        try
        {
            var dir = Path.GetDirectoryName(FilePath) ?? AppContext.BaseDirectory;
            Directory.CreateDirectory(dir);
            await File.WriteAllTextAsync(FilePath, sb.ToString(), Encoding.UTF8);
            primarySuccess = true;
        }
        catch (UnauthorizedAccessException)
        {
        }
        catch (DirectoryNotFoundException)
        {
        }
        catch (Exception)
        {
        }

        // Also write to AppData fallback (for backup)
        try
        {
            string fallbackPath = GetFallbackPath();
            var fallbackDir = Path.GetDirectoryName(fallbackPath) ?? AppContext.BaseDirectory;
            Directory.CreateDirectory(fallbackDir);
            await File.WriteAllTextAsync(fallbackPath, sb.ToString(), Encoding.UTF8);
            
            // If primary write failed, copy fallback to primary
            if (!primarySuccess)
            {
                try
                {
                    var dir = Path.GetDirectoryName(FilePath) ?? AppContext.BaseDirectory;
                    Directory.CreateDirectory(dir);
                    File.Copy(fallbackPath, FilePath, overwrite: true);
                }
                catch (Exception)
                {
                }
            }
        }
        catch (Exception)
        {
            if (!primarySuccess)
            {
                throw;
            }
        }
    }

    public async Task SaveAllAsync(IEnumerable<GiftInfo> gifts) => await SaveAsync(gifts);
}

// Background service that updates prices every 12 hours
class PriceUpdateService : BackgroundService
{
    private readonly TimeSpan _updateInterval = TimeSpan.FromHours(12);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Console.WriteLine("Price update service started. Will update prices every 12 hours.");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_updateInterval, stoppingToken);
                
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Running automatic price update...");
                await UpdateAllPricesAsync();
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Price update completed.");
            }
            catch (TaskCanceledException)
            {
                // Expected when stopping
                break;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Error during automatic price update: {ex.Message}");
            }
        }
    }

    private async Task UpdateAllPricesAsync()
    {
        string[] files =
        {
            @"\Jannicgifts.txt",
            @"\Katrinegifts.txt",
            @"\Rudgifts.txt",
            @"\Hjaltegifts.txt"
        };

        foreach (var file in files)
        {
            await UpdateFileAsync(file);
        }
    }

    private async Task UpdateFileAsync(string filePath)
    {
        var storage = new FileStorage(filePath);
        var list = await storage.LoadAsync();

        if (list.Count == 0) return;

        bool anyChanged = false;
        for (int i = 0; i < list.Count; i++)
        {
            var item = list[i];
            
            // Skip if manually priced or updated recently (within last 11 hours)
            if (item.IsManualPrice || (DateTime.Now - item.LastPriceUpdate).TotalHours < 11)
            {
                continue;
            }

            try
            {
                var (newName, newPrice, _) = await ProductInformation.GetProductInfoAsync(item.URl);
                
                if (newName != item.Produkt) item.Produkt = newName;
                if (Math.Abs(newPrice - item.Price) > 0.0001f)
                {
                    item.Price = newPrice;
                    anyChanged = true;
                }
                item.LastPriceUpdate = DateTime.Now;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Failed to update product '{item.Produkt}': {ex.Message}");
            }
        }

        if (anyChanged)
        {
            await storage.SaveAllAsync(list);
        }
    }
}

public class GiftInfo
{
    public GiftInfo(string produkt, float price, string uRl, string? priceRunnerProductId = null, DateTime? lastPriceUpdate = null, string? shopName = null, bool isManualPrice = false)
    {
        Produkt = produkt;
        Price = price;
        URl = uRl;
        PriceRunnerProductId = priceRunnerProductId ?? string.Empty;
        LastPriceUpdate = lastPriceUpdate ?? DateTime.MinValue;
        ShopName = shopName ?? string.Empty;
        IsManualPrice = isManualPrice;
    }
    public string Produkt { get; set; }
    public float Price { get; set; }
    public string URl { get; set; }
    public string PriceRunnerProductId { get; set; }
    public DateTime LastPriceUpdate { get; set; }
    public string ShopName { get; set; }
    public bool IsManualPrice { get; set; }
}
class Website
{
    public List<GiftInfo> WebsiteList = new List<GiftInfo>();
    
    // Start a minimal HTTP API to serve gift lists as JSON.
    // Endpoints:
    //  GET /gifts/{person}  -> returns that person's list
    //  GET /gifts            -> returns combined list
    public static async Task StartApiAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddCors();
        builder.Services.AddHostedService<PriceUpdateService>();
        
        // Disable ASP.NET Core logging to keep console clean
        builder.Logging.ClearProviders();
        
        // listen only on localhost to avoid exposing the service publicly
        builder.WebHost.UseUrls("http://*:5000");

        var app = builder.Build();

        app.UseCors(policy => policy.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod());

        app.MapGet("/gifts/{person}", async (string person) =>
        {
            // Prefer files placed next to the app (publish folder) so containers can include or mount them.
            string fileName = person.ToLower() switch
            {
                "rud" => "Rudgifts.txt",
                "katrine" => "Katrinegifts.txt",
                "jannic" => "Jannicgifts.txt",
                "hjalte" => "Hjaltegifts.txt",
                _ => "gifts.txt"
            };

            // Build AppData fallback path
            string userDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string appDataPath = Path.Combine(userDir, "JulegaveListe", fileName);

            var candidates = new[]
            {
                // Prioritize AppData location (where data is reliably written)
                appDataPath,
                // container-friendly data directory (mounted volume)
                Path.Combine(Path.DirectorySeparatorChar == '/' ? "/data" : "/data", fileName),
                Path.Combine(AppContext.BaseDirectory, fileName),
                Path.Combine(Directory.GetCurrentDirectory(), fileName),
                Path.Combine("C:\\", fileName)
            };

            string chosen = candidates.FirstOrDefault(File.Exists) ?? appDataPath;
            var storage = new FileStorage(chosen);
            var list = await storage.LoadAsync();
            return Results.Json(list);
        });

    app.MapGet("/gifts", async () =>
        {
            string[] names = { "Jannicgifts.txt", "Katrinegifts.txt", "Rudgifts.txt", "Hjaltegifts.txt" };
            var all = new List<GiftInfo>();
            
            // Build AppData base path
            string userDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string appDataDir = Path.Combine(userDir, "JulegaveListe");
            
            foreach (var name in names)
            {
                string appDataPath = Path.Combine(appDataDir, name);
                var candidate = appDataPath;
                
                // Try fallback locations if AppData doesn't exist
                if (!File.Exists(candidate)) candidate = Path.Combine(Path.DirectorySeparatorChar == '/' ? "/data" : "/data", name);
                if (!File.Exists(candidate)) candidate = Path.Combine(AppContext.BaseDirectory, name);
                if (!File.Exists(candidate)) candidate = Path.Combine(Directory.GetCurrentDirectory(), name);
                if (!File.Exists(candidate)) candidate = Path.Combine("C:\\", name);
                
                var s = new FileStorage(candidate);
                all.AddRange(await s.LoadAsync());
            }
            return Results.Json(all);
        });

        // Serve the website.html file at the root if present so visiting http://localhost:5000/ shows the web UI.
        app.MapGet("/", async () =>
        {
            // Try several likely locations for website.html (project root when running from IDE, current dir when published, and a known workspace path).
            var candidates = new[]
            {
                Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "website.html")),
                Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "website.html")),
                Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "website.html")),
                Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "website.html")),
                Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "website.html")),
                // fallback explicit workspace path used in this project
                Path.GetFullPath(@"\Julegave\website.html")
            };

            foreach (var p in candidates)
            {
                try
                {
                    if (File.Exists(p))
                    {
                        var bytes = await File.ReadAllBytesAsync(p);
                        return Results.File(bytes, "text/html");
                    }
                }
                catch
                {
                    // ignore IO issues and try next candidate
                }
            }

            return Results.Text("JulegaveListe API running. Endpoints: /gifts and /gifts/{person} (rud,katrine,jannic,hjalte).\nNote: website.html not found on disk.");
        });

        await app.RunAsync();
    }
    static async Task Main(string[] args)
    {
        // start the simple API in background FIRST before any prompts
        _ = StartApiAsync();
        
        // Give the API a moment to start
        await Task.Delay(500);
        
        Console.WriteLine("Started local API at http://localhost:5000 (for website to fetch lists)");
        Console.WriteLine("Open http://localhost:5000 in your browser to view the gift lists.");
        Console.WriteLine("Background price updates enabled (every 12 hours)");
        Console.WriteLine("\nConsole menu available. API continues running in background.\n");

        var website = new Website();

        while (true)
        {
            Console.Clear();
            Console.WriteLine("API running at http://localhost:5000\n");
            Console.WriteLine("1: Add To List \n2: Remove In List \n3: Manual Update \n4: Switch to Daemon Mode");
            string? input = Console.ReadLine();

            // If there's no console attached (e.g., running inside a container without stdin),
            // Console.ReadLine() returns null. In that case keep the process alive and let the
            // background API serve requests until the container is stopped.
            if (input is null)
            {
                Console.WriteLine("No console input available. Running API only; container will keep running until stopped.");
                await Task.Delay(Timeout.Infinite);
            }
            if (input == "4")
            {
                Console.Clear();
                Console.WriteLine("Starting daemon mode...");
                Console.WriteLine("\nThe program will now run in the background.");
                Console.WriteLine("API: http://localhost:5000");
                Console.WriteLine("Price updates: Every 12 hours");
                Console.WriteLine("\nPress any key to start daemon mode...");
                Console.ReadKey(true);
                
                // Detach from terminal and run as daemon
                if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
                {
                    // Close standard input/output/error to detach from terminal
                    Console.WriteLine("\n[Daemon Mode] Running in background. Logging to syslog.");
                    Console.WriteLine("To stop: kill $(pgrep -f JulegaveListe) or systemctl stop julegave");
                    
                    // Redirect console output to /dev/null or log file
                    var logPath = "/var/log/julegaveliste.log";
                    try
                    {
                        // Try to write to system log location
                        if (Directory.Exists("/var/log"))
                        {
                            File.AppendAllText(logPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] Daemon mode started\n");
                            Console.SetOut(new StreamWriter(logPath, true) { AutoFlush = true });
                            Console.SetError(new StreamWriter(logPath, true) { AutoFlush = true });
                        }
                    }
                    catch
                    {
                        // Fall back to user's home directory
                        var homePath = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                        logPath = Path.Combine(homePath, ".julegaveliste.log");
                        File.AppendAllText(logPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] Daemon mode started\n");
                        Console.SetOut(new StreamWriter(logPath, true) { AutoFlush = true });
                        Console.SetError(new StreamWriter(logPath, true) { AutoFlush = true });
                    }
                    
                    // Suppress console input
                    Console.SetIn(new StreamReader(Stream.Null));
                }
                else if (OperatingSystem.IsWindows())
                {
                    // Hide the console window on Windows
                    var handle = NativeMethods.GetConsoleWindow();
                    NativeMethods.ShowWindow(handle, 0); // 0 = SW_HIDE
                }
                
                Console.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] Daemon mode active - API running on http://localhost:5000");
                Console.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] Background price updates enabled (every 12 hours)");
                
                // Run indefinitely in background as a daemon
                await Task.Delay(Timeout.Infinite);
            }
            else if (input == "1")
            {
                Console.Clear();
                Console.WriteLine("Hvem skal der tilføjes til? \n 1: Jannic \n 2: Katrine \n 3: Rud \n 4: Hjalte");
                string? person = Console.ReadLine();
                if (person == "1") 
                {
                    Console.Clear();
                    await website.ShowGiftList(@"\Jannicgifts.txt");
                    await website.AddWebsite(@"\Jannicgifts.txt");

                }
                else if (person == "2")
                {
                    Console.Clear();
                    await website.ShowGiftList(@"\Katrinegifts.txt");
                    await website.AddWebsite(@"\Katrinegifts.txt");
                }
                else if (person == "3")
                {
                    Console.Clear();
                    await website.ShowGiftList(@"\Rudgifts.txt");
                    await website.AddWebsite(@"\Rudgifts.txt");
                }
                else if (person == "4")
                {
                    Console.Clear();
                    await website.ShowGiftList(@"\Hjaltegifts.txt");
                    await website.AddWebsite(@"\Hjaltegifts.txt");
                }
                else
                {
                    return;
                }
            }
            else if (input == "2")
            {
                Console.Clear();
                Console.WriteLine("Hvem skal der fjernes fra? \n 1: Jannic \n 2: Katrine \n 3: Rud \n 4: Hjalte");
                string? person = Console.ReadLine();
                if (person == "1")
                {
                    Console.Clear();
                    await website.ShowGiftList(@"\Jannicgifts.txt");
                    await website.RemoveEntryAsync(@"\Jannicgifts.txt");
                }
                else if (person == "2")
                {
                    Console.Clear();
                    await website.ShowGiftList(@"\Katrinegifts.txt");
                    await website.RemoveEntryAsync(@"\Katrinegifts.txt");
                }
                else if (person == "3")
                {
                    Console.Clear();
                    await website.ShowGiftList(@"\Rudgifts.txt");
                    await website.RemoveEntryAsync(@"\Rudgifts.txt");
                }
                else if (person == "4")
                {
                    Console.Clear();
                    await website.ShowGiftList(@"\Hjaltegifts.txt");
                    await website.RemoveEntryAsync(@"\Hjaltegifts.txt");
                }
                else
                {
                    return;
                }
            }
            else if (input == "3")
            {
                await website.UpdatePris();
            }
            else
            {
                return;
            }
        }
    }
    public async Task RemoveEntryAsync(string FileLocation)
    {
        var storage = new FileStorage(FileLocation);
        var list = await storage.LoadAsync();

        if (list.Count == 0)
        {
            Console.WriteLine("No entries to remove.");
            return;
        }

        // show items with indexes
        for (int i = 0; i < list.Count; i++)
        {
            var g = list[i];
            Console.WriteLine($"{i + 1}: {g.Produkt} | {g.Price} kr | {g.URl}");
        }

        Console.WriteLine();
        Console.WriteLine("Enter index, product name or URL to remove (leave empty to cancel):");
        string? input = Console.ReadLine();
        if (string.IsNullOrWhiteSpace(input))
        {
            Console.WriteLine("Canceled.");
            return;
        }

        // try index
        if (int.TryParse(input.Trim(), out int idx))
        {
            if (idx >= 1 && idx <= list.Count)
            {
                var removed = list[idx - 1];
                list.RemoveAt(idx - 1);
                await storage.SaveAllAsync(list);
                WebsiteList = list;
                Console.WriteLine($"Removed entry #{idx}: {removed.Produkt}");
                return;
            }
        }

        // try exact product or url match (case-insensitive)
        int foundIndex = list.FindIndex(g => string.Equals(g.Produkt, input, StringComparison.OrdinalIgnoreCase)
                                         || string.Equals(g.URl, input, StringComparison.OrdinalIgnoreCase));

        if (foundIndex >= 0)
        {
            var removed = list[foundIndex];
            list.RemoveAt(foundIndex);
            await storage.SaveAllAsync(list);
            WebsiteList = list;
            Console.WriteLine($"Removed: {removed.Produkt}");
            return;
        }

        // try partial matches (product contains or url contains)
        var partialMatches = new List<(int Index, GiftInfo Item)>();
        for (int i = 0; i < list.Count; i++)
        {
            var g = list[i];
            if ((!string.IsNullOrEmpty(g.Produkt) && g.Produkt.IndexOf(input, StringComparison.OrdinalIgnoreCase) >= 0) ||
                (!string.IsNullOrEmpty(g.URl) && g.URl.IndexOf(input, StringComparison.OrdinalIgnoreCase) >= 0))
            {
                partialMatches.Add((i, g));
            }
        }

        if (partialMatches.Count == 0)
        {
            Console.WriteLine("No matching entry found.");
            return;
        }

        if (partialMatches.Count == 1)
        {
            var (i, g) = partialMatches[0];
            list.RemoveAt(i);
            await storage.SaveAllAsync(list);
            WebsiteList = list;
            Console.WriteLine($"Removed: {g.Produkt}");
            return;
        }

        // multiple partial matches -> ask user to pick one
        Console.WriteLine("Multiple matches found, choose number to remove:");
        for (int m = 0; m < partialMatches.Count; m++)
        {
            var (i, g) = partialMatches[m];
            Console.WriteLine($"{m + 1}: {g.Produkt} | {g.Price} kr | {g.URl}");
        }

        string? pick = Console.ReadLine();
        if (int.TryParse(pick, out int p) && p >= 1 && p <= partialMatches.Count)
        {
            var toRemove = partialMatches[p - 1];
            list.RemoveAt(toRemove.Index);
            await storage.SaveAllAsync(list);
            WebsiteList = list;
            Console.WriteLine($"Removed: {toRemove.Item.Produkt}");
            return;
        }

        Console.WriteLine("No removal made.");
    }
    public async Task ShowGiftList(string FileLocation)
    {
        var storage = new FileStorage(FileLocation);
        var loaded = await storage.LoadAsync();

        // update this instance so other operations see the loaded list
        WebsiteList = loaded;

        if (loaded.Count == 0)
        {
            Console.WriteLine("No gifts found.");
            return;
        }

        foreach (var gift in loaded)
        {
            Console.WriteLine($"Produkt: {gift.Produkt}, Pris: {gift.Price} kr, URL: {gift.URl}");
        }
    }
    public async Task AddWebsite(string FileLocation)
    {
        var storage = new FileStorage(FileLocation);

        // load existing entries into this instance
        WebsiteList = await storage.LoadAsync();

        while (true)
        {
            Console.WriteLine("\nEnter product URL (or leave empty to stop):");
            var url = Console.ReadLine();
            if (string.IsNullOrWhiteSpace(url))
            {
                break;
            }

            try
            {
                // Fetch product info using browser automation
                var (productName, price, isManualPrice) = await ProductInformation.GetProductInfoAsync(url);
                
                // If price not found, ask user to enter it manually
                if (isManualPrice || price == 0f)
                {
                    Console.WriteLine("\nCould not automatically detect price. Please enter price manually:");
                    while (true)
                    {
                        Console.Write("Price (kr): ");
                        var priceInput = Console.ReadLine();
                        if (float.TryParse(priceInput, NumberStyles.Any, CultureInfo.InvariantCulture, out float manualPrice) && manualPrice >= 0)
                        {
                            price = manualPrice;
                            isManualPrice = true;
                            break;
                        }
                        Console.WriteLine("Invalid price. Please enter a valid number.");
                    }
                }
                
                var gift = new GiftInfo(
                    productName,
                    price,
                    url,
                    string.Empty,  // No PriceRunner ID
                    DateTime.Now,
                    string.Empty,  // No shop name
                    isManualPrice
                );

                WebsiteList.Add(gift);
                await storage.AppendAsync(gift);
                Console.WriteLine($"Added: {gift.Produkt} — {gift.Price} kr{(isManualPrice ? " (manual price)" : "")}");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Failed to fetch product: {ex.Message}");
            }
        }
    }
    public async Task UpdatePris()
    {
        string[] files =
        {
            @"C:\Jannicgifts.txt",
            @"C:\Katrinegifts.txt",
            @"C:\Rudgifts.txt",
            @"C:\Hjaltegifts.txt"
        };

        foreach (var file in files)
        {
            await UpdateFileAsync(file);
        }
    }
    private async Task UpdateFileAsync(string filePath)
    {
        var storage = new FileStorage(filePath);
        var list = await storage.LoadAsync();

        if (list.Count == 0)
        {
            Console.WriteLine($"No entries found in {filePath}.");
            return;
        }

        bool anyChanged = false;
        for (int i = 0; i < list.Count; i++)
        {
            var item = list[i];
            
            // Skip if no URL
            if (string.IsNullOrWhiteSpace(item.URl))
            {
                Console.WriteLine($"Skipping entry without URL at index {i} in {filePath}.");
                continue;
            }

            try
            {
                // Skip items with manually set prices
                if (item.IsManualPrice)
                {
                    Console.WriteLine($"Skipping '{item.Produkt}' (manual price)");
                    continue;
                }
                
                // Use browser automation for all URLs
                var (scrapedName, scrapedPrice, _) = await ProductInformation.GetProductInfoAsync(item.URl);

                // update name and price if they changed
                if (scrapedName != item.Produkt) item.Produkt = scrapedName;
                if (Math.Abs(scrapedPrice - item.Price) > 0.0001f)
                {
                    Console.WriteLine($"Price updated for {item.Produkt}: {item.Price} kr -> {scrapedPrice} kr");
                    item.Price = scrapedPrice;
                    anyChanged = true;
                }
                item.LastPriceUpdate = DateTime.Now;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Failed to update URL '{item.URl}' in {filePath}: {ex.Message}");
            }
        }

        if (anyChanged)
        {
            await storage.SaveAllAsync(list);
            Console.WriteLine($"Saved updates to {filePath} ({list.Count} entries).");
        }
        else
        {
            Console.WriteLine($"No price changes detected for {filePath}.");
        }
    }
}