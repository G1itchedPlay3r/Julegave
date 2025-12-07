using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Data.Sqlite;
using PuppeteerSharp;

// Browser automation service using PuppeteerSharp
class BrowserAutomation
{
    private static IBrowser? _browser;
    private static readonly SemaphoreSlim _semaphore = new SemaphoreSlim(1, 1);
    private static bool _browserInitialized = false;

    public static async Task<IBrowser> GetBrowserAsync()
    {
        await _semaphore.WaitAsync();
        try
        {
            if (_browser == null || !_browser.IsConnected)
            {
                if (!_browserInitialized)
                {
                    Console.WriteLine("Downloading Chrome browser (first run only)...");
                    var browserFetcher = new BrowserFetcher(SupportedBrowser.Chrome);
                    await browserFetcher.DownloadAsync();
                    _browserInitialized = true;
                }

                Console.WriteLine("Launching browser...");
                _browser = await Puppeteer.LaunchAsync(new LaunchOptions
                {
                    Browser = SupportedBrowser.Chrome,
                    Headless = true,
                    Args = new[]
                    {
                        "--no-sandbox",
                        "--disable-setuid-sandbox",
                        "--disable-dev-shm-usage",
                        "--disable-gpu",
                        "--disable-software-rasterizer",
                        "--disable-extensions",
                        "--disable-images",
                        "--blink-settings=imagesEnabled=false",
                        "--disable-webgl",
                        "--lang=da-DK",
                        "--single-process"
                    }
                });
            }
            return _browser;
        }
        finally
        {
            _semaphore.Release();
        }
    }

    public static async Task CloseBrowserAsync()
    {
        await _semaphore.WaitAsync();
        try
        {
            if (_browser != null)
            {
                await _browser.CloseAsync();
                _browser.Dispose();
                _browser = null;
            }
        }
        finally
        {
            _semaphore.Release();
        }
    }

    public static async Task<(string productName, float price, bool isManualPrice)> GetProductInfoWithBrowser(string url)
    {
        Exception? lastException = null;

        for (int attempt = 0; attempt < 3; attempt++)
        {
            IPage? page = null;
            try
            {
                Console.WriteLine($"Attempt {attempt + 1}/3: Getting product info from {url}");
                
                var browser = await GetBrowserAsync();
                page = await browser.NewPageAsync();
                
                // Set user agent and viewport
                await page.SetUserAgentAsync("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36");
                await page.SetViewportAsync(new ViewPortOptions { Width = 1920, Height = 1080 });
                
                Console.WriteLine($"Loading page with browser...");
                await page.GoToAsync(url, new NavigationOptions { WaitUntil = new[] { WaitUntilNavigation.DOMContentLoaded }, Timeout = 30000 });
                
                // Wait for page to render
                await Task.Delay(2000);

                // Try to find product name
                string productName = "Ukendt produkt";
                
                // First, try page title
                var pageTitle = await page.GetTitleAsync();
                if (!string.IsNullOrWhiteSpace(pageTitle) && !pageTitle.Equals("Forside", StringComparison.OrdinalIgnoreCase))
                {
                    var titleParts = pageTitle.Split(new[] { " - ", " | ", " | " }, StringSplitOptions.None);
                    if (titleParts.Length > 0 && titleParts[0].Trim().Length > 5)
                    {
                        productName = titleParts[0].Trim();
                        Console.WriteLine($"✓ Found name from title: {productName}");
                        goto PriceSearch;
                    }
                }

                // Try various selectors for product name
                string[] nameSelectors = {
                    "h1[product-display-name]",
                    "[product-display-name]",
                    "h1[itemprop='name']",
                    "h1.product-name",
                    "h1.product-title",
                    ".product-name h1",
                    ".product-title h1",
                    "h1.ProductPage_title",
                    ".ProductPage h1",
                    "main h1",
                    "article h1",
                    "[data-product-name]"
                };

                foreach (var selector in nameSelectors)
                {
                    try
                    {
                        var element = await page.QuerySelectorAsync(selector);
                        if (element != null)
                        {
                            // Try attribute first
                            var attrName = await element.GetPropertyAsync("product-display-name");
                            if (attrName == null)
                                attrName = await element.GetPropertyAsync("data-product-name");
                            
                            if (attrName != null)
                            {
                                var nameValue = await attrName.JsonValueAsync<string>();
                                if (!string.IsNullOrWhiteSpace(nameValue) && !nameValue.Equals("Forside", StringComparison.OrdinalIgnoreCase))
                                {
                                    productName = nameValue;
                                    Console.WriteLine($"✓ Found name from attribute: {productName}");
                                    goto PriceSearch;
                                }
                            }

                            // Try text content
                            var textProp = await element.GetPropertyAsync("textContent");
                            if (textProp != null)
                            {
                                var text = (await textProp.JsonValueAsync<string>())?.Trim();
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
                    }
                    catch { continue; }
                }

                if (productName == "Ukendt produkt")
                {
                    Console.WriteLine("⚠️  Could not find product name on page");
                }

                PriceSearch:

                // Try to find price
                float price = 0f;
                string[] priceSelectors = {
                    ".site-currency-attention",
                    "[itemprop='price']",
                    ".price-now",
                    ".current-price",
                    ".sale-price",
                    ".product-price span",
                    ".product-price",
                    ".price span",
                    ".price",
                    "[data-price]"
                };

                foreach (var selector in priceSelectors)
                {
                    try
                    {
                        var element = await page.QuerySelectorAsync(selector);
                        if (element != null)
                        {
                            // Try content/data attributes first
                            var contentProp = await element.GetPropertyAsync("content");
                            if (contentProp == null)
                                contentProp = await element.GetPropertyAsync("data-price");

                            if (contentProp != null)
                            {
                                var attrPrice = await contentProp.JsonValueAsync<string>();
                                if (!string.IsNullOrWhiteSpace(attrPrice) && 
                                    float.TryParse(attrPrice, NumberStyles.Any, CultureInfo.InvariantCulture, out float attrPriceValue))
                                {
                                    price = attrPriceValue;
                                    Console.WriteLine($"✓ Found price: {price} kr");
                                    goto FoundPrice;
                                }
                            }

                            // Try visible text
                            var textProp = await element.GetPropertyAsync("textContent");
                            if (textProp != null)
                            {
                                var text = (await textProp.JsonValueAsync<string>())?.Trim();
                                if (!string.IsNullOrWhiteSpace(text))
                                {
                                    var match = Regex.Match(text, @"(\d{1,3}(?:[.,\s]\d{3})*(?:[.,]\d{2})?)");
                                    if (match.Success)
                                    {
                                        string priceText = match.Groups[1].Value;

                                        // Handle Danish format: "7.777,00"
                                        if (priceText.Contains(","))
                                        {
                                            var parts = priceText.Split(',');
                                            if (parts.Length > 1 && parts[1].Length <= 2)
                                            {
                                                priceText = parts[0];
                                            }
                                            else
                                            {
                                                priceText = priceText.Replace(",", "");
                                            }
                                        }

                                        // Handle format with dots
                                        if (priceText.Contains("."))
                                        {
                                            var parts = priceText.Split('.');
                                            if (parts.Length > 1 && parts[1].Length <= 2)
                                            {
                                                priceText = parts[0];
                                            }
                                            else
                                            {
                                                priceText = priceText.Replace(".", "");
                                            }
                                        }

                                        priceText = priceText.Replace(" ", "").Trim();

                                        if (float.TryParse(priceText, NumberStyles.Any, CultureInfo.InvariantCulture, out float parsedPrice))
                                        {
                                            price = parsedPrice;
                                            Console.WriteLine($"✓ Found price: {price} kr");
                                            goto FoundPrice;
                                        }
                                    }
                                }
                            }
                        }
                    }
                    catch { continue; }
                }

                FoundPrice:

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

                await page.CloseAsync();
                return (productName, price, isManualPrice);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Browser error (attempt {attempt + 1}/3): {ex.Message}");
                lastException = ex;

                if (page != null)
                {
                    try { await page.CloseAsync(); } catch { }
                }

                if (attempt < 2)
                {
                    await Task.Delay(2000);
                }
            }
        }

        throw new Exception($"Failed to get product info after 3 attempts. Last error: {lastException?.Message ?? "Unknown"}");
    }
}

class ProductInformation 
{
    public static async Task<(string productName, float price, bool isManualPrice)> GetProductInfoAsync(string url)
    {
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
        
        // Use the bin/Debug or bin/Release directory where the DLL actually runs
        string projectDir = AppContext.BaseDirectory;
        _dbPath = Path.Combine(projectDir, "gifts.db");
        
        Console.WriteLine($"[DATABASE] Using database path: {_dbPath}");
        Console.WriteLine($"[DATABASE] AppContext.BaseDirectory: {AppContext.BaseDirectory}");
        
        // Check if database exists in old AppData location and migrate it
        MigrateFromAppDataIfNeeded();
        
        InitializeDatabase();
    }
    
    private void MigrateFromAppDataIfNeeded()
    {
        // If database already exists in project folder, no migration needed
        if (File.Exists(_dbPath))
        {
            return;
        }
        
        // Check old AppData location
        string? oldDbPath = null;
        try
        {
            if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
            {
                // Check common Linux locations
                string[] oldPaths = {
                    "/var/lib/julegaveliste/gifts.db",
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "JulegaveListe", "gifts.db")
                };
                
                foreach (var path in oldPaths)
                {
                    if (File.Exists(path))
                    {
                        oldDbPath = path;
                        break;
                    }
                }
            }
            else
            {
                // Windows AppData location
                oldDbPath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "JulegaveListe",
                    "gifts.db"
                );
            }
            
            if (oldDbPath != null && File.Exists(oldDbPath))
            {
                Console.WriteLine($"[DATABASE] Migrating database from {oldDbPath} to {_dbPath}");
                File.Copy(oldDbPath, _dbPath, overwrite: false);
                Console.WriteLine($"[DATABASE] Migration successful");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[DATABASE] Migration failed (will start fresh): {ex.Message}");
        }
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
        // Database is stored in project folder
        return Path.Combine(AppContext.BaseDirectory, "gifts.db");
    }
}

// Background service that updates prices every 12 hours
class PriceUpdateService : BackgroundService
{
    private readonly TimeSpan _updateInterval = TimeSpan.FromHours(12);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Console.WriteLine("Price update service started. Will update prices every 12 hours.");

        // Run update immediately on first startup
        try
        {
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Running initial price update...");
            await UpdateAllPricesAsync();
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Initial price update completed.");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error during initial price update: {ex.Message}");
        }

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
                
                // Only update price if it's valid (not 0) and lower than current price
                if (newPrice > 0 && newPrice < item.Price && Math.Abs(newPrice - item.Price) > 0.0001f)
                {
                    Console.WriteLine($"[PRICE-UPDATE] {item.Produkt}: {item.Price} kr → {newPrice} kr (savings: {item.Price - newPrice} kr)");
                    item.Price = newPrice;
                    anyChanged = true;
                }
                else if (newPrice == 0)
                {
                    Console.WriteLine($"[PRICE-UPDATE] {item.Produkt}: Price returned as 0, keeping original {item.Price} kr");
                }
                else if (newPrice > item.Price)
                {
                    Console.WriteLine($"[PRICE-UPDATE] {item.Produkt}: Price increased to {newPrice} kr, keeping original {item.Price} kr");
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
                
                Console.WriteLine($"[REMOVE-GIFTS] Before removal: {list.Count} gifts for {personKey}");
                Console.WriteLine($"[REMOVE-GIFTS] URLs to remove: {string.Join(", ", request.Urls)}");
                
                int initialCount = list.Count;
                
                // Remove items that match the URLs (trim URLs for comparison)
                var urlsToRemove = request.Urls.Select(u => u?.Trim()).Where(u => !string.IsNullOrEmpty(u)).ToList();
                int removedCount = list.RemoveAll(g => urlsToRemove.Contains(g.URl?.Trim()));
                
                Console.WriteLine($"[REMOVE-GIFTS] Removed {removedCount} gifts. After removal: {list.Count} gifts");
                
                await storage.SaveAllAsync(list);
                
                // Verify the save
                var verifyList = await storage.LoadAsync();
                Console.WriteLine($"[REMOVE-GIFTS] Verified after save: {verifyList.Count} gifts");
                
                return Results.Ok(new { success = true, message = $"Removed {removedCount} gift(s)", removedCount, remainingCount = list.Count });
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[REMOVE-GIFTS] Error: {ex.Message}");
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

            // Try several likely locations for website.html - prioritize deployment directory
            var candidates = new[]
            {
                Path.Combine(AppContext.BaseDirectory, "website.html"),  // Deployed with app
                Path.Combine(Directory.GetCurrentDirectory(), "website.html"),  // Current directory
                "/home/rud/Julegave/JulegaveListe/website.html",  // Linux deployment actual path
                Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "website.html"),  // IDE debug
                Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "website.html"),  // IDE debug alternate
                @"c:\Users\rud\OneDrive\Skrivebord\Julegave-main\JulegaveListe\JulegaveListe\website.html",  // Windows dev
                "/home/rud/Julegave/website.html"  // Linux alternate
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
                Path.Combine(AppContext.BaseDirectory, "admin.html"),  // Deployed with app
                Path.Combine(Directory.GetCurrentDirectory(), "admin.html"),  // Current directory
                "/home/rud/Julegave/JulegaveListe/admin.html",  // Linux deployment actual path
                Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "admin.html"),  // IDE debug
                Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "admin.html"),  // IDE debug alternate
                @"c:\Users\rud\OneDrive\Skrivebord\Julegave-main\JulegaveListe\JulegaveListe\admin.html",  // Windows dev
                "/home/rud/Julegave/admin.html"  // Linux alternate
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
