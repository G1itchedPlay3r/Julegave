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
using Microsoft.Data.Sqlite;

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
            // Check if driver is valid, if not recreate it
            if (_driver != null)
            {
                try
                {
                    // Test if session is still valid by checking multiple properties
                    var _ = _driver.WindowHandles;
                    var __ = _driver.CurrentWindowHandle;
                    // If we can access these, session is valid
                }
                catch (Exception ex)
                {
                    // Session invalid (browser closed, session expired, etc.)
                    Console.WriteLine($"⚠️  WebDriver session invalid ({ex.GetType().Name}), recreating...");
                    try { _driver.Quit(); } catch { }
                    try { _driver.Dispose(); } catch { }
                    _driver = null;
                }
            }
            
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
        // Retry logic in case of session errors
        Exception? lastException = null;
        
        for (int attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                Console.WriteLine($"Attempt {attempt + 1}/3: Getting product info from {url}");
                var driver = GetDriver();
                
                Console.WriteLine($"Loading page with browser...");
                driver.Navigate().GoToUrl(url);
                
                // Wait for page to fully load (including JavaScript)
                await Task.Delay(3000);

            // Try to find product name - prioritize common e-commerce patterns
            string productName = "Ukendt produkt";
            
            // First, try to get from page title (often most reliable)
            try
            {
                string pageTitle = driver.Title;
                if (!string.IsNullOrWhiteSpace(pageTitle) && !pageTitle.Equals("Forside", StringComparison.OrdinalIgnoreCase))
                {
                    // Remove common suffixes like " - Proshop", " | Shop name", etc.
                    var titleParts = pageTitle.Split(new[] { " - ", " | ", " | " }, StringSplitOptions.None);
                    if (titleParts.Length > 0 && titleParts[0].Trim().Length > 5)
                    {
                        productName = titleParts[0].Trim();
                        Console.WriteLine($"✓ Found name from title: {productName}");
                        goto PriceSearch; // Skip other name searches if we found it
                    }
                }
            }
            catch { }
            
            string[] nameSelectors = {
                "h1[product-display-name]",  // Proshop specific
                "[product-display-name]",  // Proshop attribute
                "h1[itemprop='name']",  // Schema.org
                "h1.product-name",
                "h1.product-title",
                ".product-name h1",
                ".product-title h1",
                "h1.ProductPage_title",  // Some sites use this
                ".ProductPage h1",  // Product page heading
                "main h1",  // Main content h1
                "article h1",  // Article heading
                "[data-product-name]",  // Data attribute
            };

            foreach (var selector in nameSelectors)
            {
                try
                {
                    var elements = driver.FindElements(By.CssSelector(selector));
                    foreach (var element in elements)
                    {
                        // Try attribute first (Proshop uses this)
                        string? attrName = element.GetAttribute("product-display-name") ?? element.GetAttribute("data-product-name");
                        if (!string.IsNullOrWhiteSpace(attrName) && !attrName.Equals("Forside", StringComparison.OrdinalIgnoreCase))
                        {
                            productName = attrName;
                            Console.WriteLine($"✓ Found name from attribute: {productName}");
                            goto PriceSearch;
                        }
                        
                        // Try text content
                        string? text = element.Text?.Trim();
                        if (!string.IsNullOrWhiteSpace(text) && 
                            !text.Equals("Forside", StringComparison.OrdinalIgnoreCase) && 
                            text.Length > 5)
                        {
                            productName = text;
                            Console.WriteLine($"✓ Found name from element: {productName}");
                            goto PriceSearch;
                        }
                    }
                }
                catch { continue; }
            }
            
            if (productName == "Ukendt produkt")
            {
                Console.WriteLine("⚠️  Could not find product name on page");
            }

            PriceSearch:

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
                            
                            // Handle different formats:
                            // Danish: "7.777,00" (dot = thousands, comma = decimal)
                            // English: "7,777.00" (comma = thousands, dot = decimal)
                            // We want to ignore decimals and keep whole number only
                            
                            // If there's a comma, check if it's followed by 2 digits (decimal separator)
                            if (priceText.Contains(","))
                            {
                                var parts = priceText.Split(',');
                                if (parts.Length > 1 && parts[1].Length <= 2)
                                {
                                    // Danish format: comma is decimal separator, ignore it
                                    priceText = parts[0];
                                }
                                else
                                {
                                    // Comma is thousands separator, keep all
                                    priceText = priceText.Replace(",", "");
                                }
                            }
                            
                            // If there's a dot, check if it's followed by 2 digits (decimal separator)
                            if (priceText.Contains("."))
                            {
                                var parts = priceText.Split('.');
                                if (parts.Length > 1 && parts[1].Length <= 2)
                                {
                                    // Dot is decimal separator (English format), ignore decimals
                                    priceText = parts[0];
                                }
                                else
                                {
                                    // Dot is thousands separator (Danish format), remove it
                                    priceText = priceText.Replace(".", "");
                                }
                            }
                            
                            priceText = priceText.Replace(" ", "").Trim();
                            
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
                Console.WriteLine($"❌ Browser error (attempt {attempt + 1}/3): {ex.Message}");
                lastException = ex;
                
                // Force driver recreation on error
                lock (_lock)
                {
                    if (_driver != null)
                    {
                        try { _driver.Quit(); } catch { }
                        try { _driver.Dispose(); } catch { }
                        _driver = null;
                    }
                }
                
                if (attempt < 2)
                {
                    // Wait before retry
                    await Task.Delay(2000);
                }
            }
        }
        
        // All attempts failed
        throw new Exception($"Failed to get product info after 3 attempts. Last error: {lastException?.Message ?? "Unknown"}");
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
class DatabaseStorage
{
    private readonly string _dbPath;
    private readonly string _person;

    public DatabaseStorage(string person)
    {
        if (string.IsNullOrWhiteSpace(person)) throw new ArgumentNullException(nameof(person));
        _person = person.ToLowerInvariant();
        
        // Store database in AppData
        string appDataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "JulegaveListe"
        );
        Directory.CreateDirectory(appDataDir);
        _dbPath = Path.Combine(appDataDir, "gifts.db");
        
        InitializeDatabase();
    }

    private void InitializeDatabase()
    {
        using var connection = new SqliteConnection($"Data Source={_dbPath}");
        connection.Open();

        var command = connection.CreateCommand();
        command.CommandText = @"
            CREATE TABLE IF NOT EXISTS gifts (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                person TEXT NOT NULL,
                produkt TEXT NOT NULL,
                price REAL NOT NULL,
                url TEXT NOT NULL,
                priceRunnerProductId TEXT NOT NULL DEFAULT '',
                lastPriceUpdate TEXT NOT NULL DEFAULT '',
                shopName TEXT NOT NULL DEFAULT '',
                isManualPrice INTEGER NOT NULL DEFAULT 0,
                productInfo TEXT NOT NULL DEFAULT '',
                isFavorite INTEGER NOT NULL DEFAULT 0
            )";
        command.ExecuteNonQuery();

        // Create index for faster person lookups
        command.CommandText = @"CREATE INDEX IF NOT EXISTS idx_person ON gifts(person)";
        command.ExecuteNonQuery();
    }

    public async Task<List<GiftInfo>> LoadAsync()
    {
        var list = new List<GiftInfo>();

        using var connection = new SqliteConnection($"Data Source={_dbPath}");
        await connection.OpenAsync();

        var command = connection.CreateCommand();
        command.CommandText = @"
            SELECT produkt, price, url, priceRunnerProductId, lastPriceUpdate, 
                   shopName, isManualPrice, productInfo, isFavorite
            FROM gifts 
            WHERE person = $person";
        command.Parameters.AddWithValue("$person", _person);

        using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var produkt = reader.GetString(0);
            var price = (float)reader.GetDouble(1);
            var url = reader.GetString(2);
            var priceRunnerProductId = reader.GetString(3);
            var lastPriceUpdateStr = reader.GetString(4);
            var shopName = reader.GetString(5);
            var isManualPrice = reader.GetInt32(6) == 1;
            var productInfo = reader.GetString(7);
            var isFavorite = reader.GetInt32(8) == 1;

            DateTime lastPriceUpdate = DateTime.MinValue;
            if (!string.IsNullOrEmpty(lastPriceUpdateStr))
            {
                DateTime.TryParse(lastPriceUpdateStr, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out lastPriceUpdate);
            }

            list.Add(new GiftInfo(
                produkt, 
                price, 
                url, 
                priceRunnerProductId, 
                lastPriceUpdate, 
                shopName, 
                isManualPrice, 
                productInfo,
                isFavorite
            ));
        }

        return list;
    }

    public async Task AppendAsync(GiftInfo gift)
    {
        using var connection = new SqliteConnection($"Data Source={_dbPath}");
        await connection.OpenAsync();

        var command = connection.CreateCommand();
        command.CommandText = @"
            INSERT INTO gifts (person, produkt, price, url, priceRunnerProductId, lastPriceUpdate, 
                              shopName, isManualPrice, productInfo, isFavorite)
            VALUES ($person, $produkt, $price, $url, $priceRunnerProductId, $lastPriceUpdate, 
                   $shopName, $isManualPrice, $productInfo, $isFavorite)";
        
        command.Parameters.AddWithValue("$person", _person);
        command.Parameters.AddWithValue("$produkt", gift.Produkt);
        command.Parameters.AddWithValue("$price", gift.Price);
        command.Parameters.AddWithValue("$url", gift.URl);
        command.Parameters.AddWithValue("$priceRunnerProductId", gift.PriceRunnerProductId ?? string.Empty);
        command.Parameters.AddWithValue("$lastPriceUpdate", gift.LastPriceUpdate.ToString("o", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$shopName", gift.ShopName ?? string.Empty);
        command.Parameters.AddWithValue("$isManualPrice", gift.IsManualPrice ? 1 : 0);
        command.Parameters.AddWithValue("$productInfo", gift.ProductInfo ?? string.Empty);
        command.Parameters.AddWithValue("$isFavorite", gift.isFavorite ? 1 : 0);

        await command.ExecuteNonQueryAsync();
    }

    public async Task SaveAsync(IEnumerable<GiftInfo> gifts)
    {
        using var connection = new SqliteConnection($"Data Source={_dbPath}");
        await connection.OpenAsync();

        using var transaction = connection.BeginTransaction();

        try
        {
            // Delete all gifts for this person
            var deleteCommand = connection.CreateCommand();
            deleteCommand.CommandText = "DELETE FROM gifts WHERE person = $person";
            deleteCommand.Parameters.AddWithValue("$person", _person);
            await deleteCommand.ExecuteNonQueryAsync();

            // Insert all gifts
            foreach (var gift in gifts)
            {
                var insertCommand = connection.CreateCommand();
                insertCommand.CommandText = @"
                    INSERT INTO gifts (person, produkt, price, url, priceRunnerProductId, lastPriceUpdate, 
                                      shopName, isManualPrice, productInfo, isFavorite)
                    VALUES ($person, $produkt, $price, $url, $priceRunnerProductId, $lastPriceUpdate, 
                           $shopName, $isManualPrice, $productInfo, $isFavorite)";
                
                insertCommand.Parameters.AddWithValue("$person", _person);
                insertCommand.Parameters.AddWithValue("$produkt", gift.Produkt);
                insertCommand.Parameters.AddWithValue("$price", gift.Price);
                insertCommand.Parameters.AddWithValue("$url", gift.URl);
                insertCommand.Parameters.AddWithValue("$priceRunnerProductId", gift.PriceRunnerProductId ?? string.Empty);
                insertCommand.Parameters.AddWithValue("$lastPriceUpdate", gift.LastPriceUpdate.ToString("o", CultureInfo.InvariantCulture));
                insertCommand.Parameters.AddWithValue("$shopName", gift.ShopName ?? string.Empty);
                insertCommand.Parameters.AddWithValue("$isManualPrice", gift.IsManualPrice ? 1 : 0);
                insertCommand.Parameters.AddWithValue("$productInfo", gift.ProductInfo ?? string.Empty);
                insertCommand.Parameters.AddWithValue("$isFavorite", gift.isFavorite ? 1 : 0);

                await insertCommand.ExecuteNonQueryAsync();
            }

            transaction.Commit();
        }
        catch
        {
            transaction.Rollback();
            throw;
        }
    }

    public async Task SaveAllAsync(IEnumerable<GiftInfo> gifts) => await SaveAsync(gifts);

    public static string GetDatabasePath()
    {
        string appDataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "JulegaveListe"
        );
        return Path.Combine(appDataDir, "gifts.db");
    }
}

// Keep FileStorage for migration purposes
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

    // Format per line: <escaped product>|<price-in-invariant-culture>|<escaped url>|<priceRunnerProductId>|<lastPriceUpdate>|<shopName>|<isManualPrice>|<isFavorite>|<productInfo>
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
        bool needsUpgrade = false;

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
                using var sr = new StreamReader(pathToRead, Encoding.UTF8);
                string? line;
                while ((line = await sr.ReadLineAsync()) != null)
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    var parts = SplitEscaped(line);
                    if (parts.Length < 2) continue;
                    string productPart = Unescape(parts[0]);
                    string pricePart = parts[1];
                    if (parts.Length < 9)
                    {
                        needsUpgrade = true;
                    }
                    string urlPart = parts.Length > 2 ? Unescape(parts[2]) : string.Empty;
                    string prIdPart = parts.Length > 3 ? Unescape(parts[3]) : string.Empty;
                    string lastUpdatePart = parts.Length > 4 ? parts[4] : string.Empty;
                    string shopNamePart = parts.Length > 5 ? Unescape(parts[5]) : string.Empty;
                    bool manualPrice = parts.Length > 6 && bool.TryParse(parts[6], out bool mp) && mp;
                    string productInfo = parts.Length > 7 ? Unescape(parts[7]) : string.Empty;
                    bool isFavorite = parts.Length > 8 && bool.TryParse(parts[8], out bool fav) && fav;

                    if (!float.TryParse(pricePart, NumberStyles.Any, CultureInfo.InvariantCulture, out float price))
                    {
                        price = 0f;
                    }
                    DateTime lastUpdate = DateTime.MinValue;
                    if (!string.IsNullOrEmpty(lastUpdatePart))
                    {
                        DateTime.TryParse(lastUpdatePart, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out lastUpdate);
                    }
                    list.Add(new GiftInfo(productPart, price, urlPart, prIdPart, lastUpdate, shopNamePart, manualPrice, productInfo, isFavorite));
                }
            }
        }
        catch (Exception)
        {
            // Ignore read errors
        }

        // Auto-upgrade old format to new format
        if (needsUpgrade && list.Count > 0)
        {
            try
            {
                await SaveAllAsync(list);
            }
            catch
            {
                // Ignore save errors
            }
        }

        return list;
    }

    public async Task AppendAsync(GiftInfo gift)
    {
        string line = $"{Escape(gift.Produkt)}|{gift.Price.ToString(CultureInfo.InvariantCulture)}|{Escape(gift.URl)}|{Escape(gift.PriceRunnerProductId)}|{gift.LastPriceUpdate.ToString("o", CultureInfo.InvariantCulture)}|{Escape(gift.ShopName)}|{gift.IsManualPrice}|{Escape(gift.ProductInfo)}{Environment.NewLine}";

        bool primarySuccess = false;
        
        // Try to write to primary location (project folder)
        try
        {
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
            string fallback = GetFallbackPath();
            string? dir = Path.GetDirectoryName(fallback);
            if (dir != null && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }
            await File.AppendAllTextAsync(fallback, line, Encoding.UTF8);
            if (!primarySuccess)
            {
                _effectiveFilePath = fallback;
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
            sb.Append(Escape(g.URl));
            sb.Append('|');
            sb.Append(Escape(g.PriceRunnerProductId));
            sb.Append('|');
            sb.Append(g.LastPriceUpdate.ToString("o", CultureInfo.InvariantCulture));
            sb.Append('|');
            sb.Append(Escape(g.ShopName));
            sb.Append('|');
            sb.Append(g.IsManualPrice);
            sb.Append('|');
            sb.Append(Escape(g.ProductInfo));
            sb.AppendLine();
        }

        bool primarySuccess = false;

        // Try to write to primary location (project folder)
        try
        {
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
            string fallback = GetFallbackPath();
            string? dir = Path.GetDirectoryName(fallback);
            if (dir != null && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }
            await File.WriteAllTextAsync(fallback, sb.ToString(), Encoding.UTF8);
            if (!primarySuccess)
            {
                _effectiveFilePath = fallback;
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
        string[] persons = { "jannic", "katrine", "rud", "hjalte" };

        foreach (var person in persons)
        {
            await UpdatePersonAsync(person);
        }
    }

    private async Task UpdatePersonAsync(string person)
    {
        var storage = new DatabaseStorage(person);
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
    public GiftInfo(string produkt, float price, string uRl, string? priceRunnerProductId = null, DateTime? lastPriceUpdate = null, string? shopName = null, bool isManualPrice = false, string? productInfo = null, bool isFavorite = false)
    {
        Produkt = produkt;
        Price = price;
        URl = uRl;
        PriceRunnerProductId = priceRunnerProductId ?? string.Empty;
        LastPriceUpdate = lastPriceUpdate ?? DateTime.MinValue;
        ShopName = shopName ?? string.Empty;
        IsManualPrice = isManualPrice;
        ProductInfo = productInfo ?? string.Empty;
        this.isFavorite = isFavorite;
    }
    public string Produkt { get; set; }
    public float Price { get; set; }
    
    [JsonPropertyName("url")]
    public string URl { get; set; }
    public bool isFavorite { get; set; }
    public string PriceRunnerProductId { get; set; }
    public DateTime LastPriceUpdate { get; set; }
    public string ShopName { get; set; }
    public bool IsManualPrice { get; set; }
    public string ProductInfo { get; set; }
}

public class ProductInfoRequest
{
    public string Url { get; set; } = string.Empty;
}

public class ProductInfoResponse
{
    public string ProductName { get; set; } = string.Empty;
    public float Price { get; set; }
    public bool IsManualPrice { get; set; }
}

public class AddGiftRequest
{
    public string Person { get; set; } = string.Empty;
    public string ProductName { get; set; } = string.Empty;
    public float Price { get; set; }
    public string Url { get; set; } = string.Empty;
    public bool IsManualPrice { get; set; }
    public string ProductInfo { get; set; } = string.Empty;
    public bool IsFavorite { get; set; }
}

public class RemoveGiftsRequest
{
    public string Person { get; set; } = string.Empty;
    public List<string> Urls { get; set; } = new List<string>();
}

public class EditGiftRequest
{
    public string Person { get; set; } = string.Empty;
    public string OriginalUrl { get; set; } = string.Empty;
    public string ProductName { get; set; } = string.Empty;
    public float Price { get; set; }
    public string Url { get; set; } = string.Empty;
    public bool IsManualPrice { get; set; }
    public string ProductInfo { get; set; } = string.Empty;
    public bool IsFavorite { get; set; }
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
        
        // Configure JSON serialization to use camelCase for JavaScript compatibility
        var jsonOptions = new System.Text.Json.JsonSerializerOptions
        {
            PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
            WriteIndented = false,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.Never
        };
        
        builder.Services.ConfigureHttpJsonOptions(options =>
        {
            options.SerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase;
        });
        
        // Disable ASP.NET Core logging to keep console clean
        builder.Logging.ClearProviders();
        
        // listen on both ports - 5000 for viewer, 5001 for admin
        builder.WebHost.UseUrls("http://*:5000", "http://*:5001");

        var app = builder.Build();

        app.UseCors(policy => policy.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod());

        // Admin API endpoint - Get product info from URL
        app.MapPost("/api/product-info", async (ProductInfoRequest request) =>
        {
            try
            {
                Console.WriteLine($"[PRODUCT-INFO] Fetching info for URL: {request.Url}");
                var (productName, price, isManualPrice) = await ProductInformation.GetProductInfoAsync(request.Url);
                Console.WriteLine($"[PRODUCT-INFO] Success - Name: {productName}, Price: {price}");
                return Results.Json(new ProductInfoResponse 
                { 
                    ProductName = productName, 
                    Price = price, 
                    IsManualPrice = isManualPrice 
                }, jsonOptions);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[PRODUCT-INFO] Error: {ex.Message}");
                Console.WriteLine($"[PRODUCT-INFO] Stack trace: {ex.StackTrace}");
                return Results.Problem($"Error fetching product info: {ex.Message}");
            }
        });

        // Admin API endpoint - Add gift to list
        app.MapPost("/api/add-gift", async (AddGiftRequest request) =>
        {
            try
            {
                string personKey = request.Person.ToLowerInvariant();
                
                if (personKey != "rud" && personKey != "katrine" && personKey != "jannic" && personKey != "hjalte")
                {
                    return Results.BadRequest("Invalid person name");
                }
                
                var storage = new DatabaseStorage(personKey);
                var list = await storage.LoadAsync();
                
                var newGift = new GiftInfo(
                    request.ProductName,
                    request.Price,
                    request.Url,
                    null,
                    DateTime.Now,
                    null,
                    request.IsManualPrice,
                    request.ProductInfo ?? string.Empty,
                    request.IsFavorite
                );
                
                list.Add(newGift);
                await storage.SaveAllAsync(list);
                
                return Results.Ok(new { success = true, message = "Gift added successfully" });
            }
            catch (Exception ex)
            {
                return Results.Problem($"Error adding gift: {ex.Message}");
            }
        });

        // Admin API endpoint - Remove gifts from list
        app.MapPost("/api/remove-gifts", async (RemoveGiftsRequest request) =>
        {
            try
            {
                string personKey = request.Person.ToLowerInvariant();
                
                if (personKey != "rud" && personKey != "katrine" && personKey != "jannic" && personKey != "hjalte")
                {
                    return Results.BadRequest("Invalid person name");
                }
                
                var storage = new DatabaseStorage(personKey);
                var list = await storage.LoadAsync();
                
                // Remove items that match the URLs
                list.RemoveAll(g => request.Urls.Contains(g.URl));
                
                await storage.SaveAllAsync(list);
                
                return Results.Ok(new { success = true, message = $"Removed {request.Urls.Count} gift(s)" });
            }
            catch (Exception ex)
            {
                return Results.Problem($"Error removing gifts: {ex.Message}");
            }
        });

        // Admin API endpoint - Edit gift
        app.MapPost("/api/edit-gift", async (EditGiftRequest request) =>
        {
            try
            {
                string personKey = request.Person.ToLowerInvariant();
                
                if (personKey != "rud" && personKey != "katrine" && personKey != "jannic" && personKey != "hjalte")
                {
                    return Results.BadRequest("Invalid person name");
                }
                
                var storage = new DatabaseStorage(personKey);
                var list = await storage.LoadAsync();
                
                // Find the gift to edit by OriginalUrl
                var gift = list.FirstOrDefault(g => g.URl == request.OriginalUrl);
                if (gift == null)
                {
                    return Results.NotFound(new { success = false, message = "Gift not found" });
                }
                
                // Update the gift properties
                gift.Produkt = request.ProductName;
                gift.Price = request.Price;
                gift.URl = request.Url;
                gift.IsManualPrice = request.IsManualPrice;
                gift.ProductInfo = request.ProductInfo ?? string.Empty;
                gift.isFavorite = request.IsFavorite;
                gift.LastPriceUpdate = DateTime.Now;
                
                await storage.SaveAllAsync(list);
                
                return Results.Ok(new { success = true, message = "Gift updated successfully" });
            }
            catch (Exception ex)
            {
                return Results.Problem($"Error editing gift: {ex.Message}");
            }
        });

        app.MapGet("/gifts/{person}", async (string person) =>
        {
            try
            {
                string personKey = person.ToLowerInvariant();
                
                if (personKey != "rud" && personKey != "katrine" && personKey != "jannic" && personKey != "hjalte")
                {
                    return Results.BadRequest("Unknown person. Valid options: rud, katrine, jannic, hjalte");
                }

                Console.WriteLine($"[API] Loading gifts for {person} from database");
                var storage = new DatabaseStorage(personKey);
                var list = await storage.LoadAsync();
                Console.WriteLine($"[API] Loaded {list.Count} gifts");
                return Results.Json(list, jsonOptions);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Error loading gifts for {person}: {ex.Message}");
                return Results.Problem($"Error loading gifts: {ex.Message}");
            }
        });

    app.MapGet("/gifts", async () =>
        {
            try
            {
                string[] persons = { "rud", "katrine", "jannic", "hjalte" };
                var all = new List<GiftInfo>();
                
                foreach (var person in persons)
                {
                    try
                    {
                        Console.WriteLine($"[API] Loading gifts for {person} from database");
                        var storage = new DatabaseStorage(person);
                        var gifts = await storage.LoadAsync();
                        Console.WriteLine($"[API] Loaded {gifts.Count} gifts for {person}");
                        all.AddRange(gifts);
                    }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine($"Error loading gifts for {person}: {ex.Message}");
                    }
                }
                
                return Results.Json(all, jsonOptions);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Error loading all gifts: {ex.Message}");
                return Results.Problem($"Error loading gifts: {ex.Message}");
            }
        });

        // Serve the website.html file at the root on port 5000
        app.MapGet("/", async (HttpContext context) =>
        {
            // On port 5001, redirect to /admin
            if (context.Request.Host.Port == 5001)
            {
                return Results.Redirect("/admin");
            }

            // Try several likely locations for website.html (project root when running from IDE, current dir when published, and a known workspace path).
            var candidates = new[]
            {
                Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "website.html")),
                Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "website.html")),
                Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "website.html")),
                Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "website.html")),
                Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "website.html")),
                @"c:\Users\rud\OneDrive\Skrivebord\Julegave-main\JulegaveListe\JulegaveListe\website.html",
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

        // Serve the admin.html file at /admin route
        app.MapGet("/admin", async (HttpContext context) =>
        {
            var candidates = new[]
            {
                Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "admin.html")),
                Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "admin.html")),
                Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "admin.html")),
                Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "admin.html")),
                Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "admin.html")),
                @"c:\Users\rud\OneDrive\Skrivebord\Julegave-main\JulegaveListe\JulegaveListe\admin.html",
                Path.GetFullPath(@"\Julegave\admin.html")
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
                catch { }
            }

            return Results.Text("Admin interface not found. Place admin.html in the project directory.");
        });

        await app.RunAsync();
    }
    static async Task Main(string[] args)
    {
        Console.WriteLine("Starting Julegave API Server...");
        Console.WriteLine("=================================");
        Console.WriteLine("Viewer:  http://localhost:5000");
        Console.WriteLine("Admin:   http://localhost:5001/admin");
        Console.WriteLine("=================================");
        Console.WriteLine("Background price updates enabled (every 12 hours)");
        Console.WriteLine("\nPress Ctrl+C to stop the server.\n");
        
        await StartApiAsync();
    }
}
