using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Serilog;

namespace TokenGenerator
{
    public class AppSettings
    {
        public string DatabasePath { get; set; } = string.Empty;

        // Method to resolve the full path, supporting both absolute and relative paths
        public string GetResolvedDatabasePath()
        {
            // If the path is already absolute, return it directly
            if (Path.IsPathRooted(DatabasePath))
            {
                return DatabasePath;
            }

            // If the path is relative, resolve it from the base directory
            string basePath = AppDomain.CurrentDomain.BaseDirectory;
            
            // Support multiple relative path scenarios
            string[] relativePaths = new[]
            {
                Path.Combine(basePath, DatabasePath),           // Relative to base directory
                Path.Combine(basePath, "..", "..", DatabasePath), // Two levels up from base directory
                Path.Combine(basePath, "..", DatabasePath),    // One level up from base directory
            };

            // Find the first path that exists
            foreach (var path in relativePaths)
            {
                string fullPath = Path.GetFullPath(path);
                if (File.Exists(fullPath))
                {
                    return fullPath;
                }
            }

            // If no existing path is found, return the first resolved path
            return Path.GetFullPath(relativePaths[0]);
        }

        // Method to load settings from a JSON file
        public static AppSettings LoadSettings(string settingsPath = null)
        {
            // Default settings file path
            settingsPath ??= Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "appsettings.json");

            // If no settings file exists, create a default one
            if (!File.Exists(settingsPath))
            {
                var defaultSettings = new AppSettings
                {
                    // Default to a relative path two folders up
                    DatabasePath = Path.Combine("..", "..", "auth.db")
                };

                // Ensure directory exists
                var directory = Path.GetDirectoryName(settingsPath);
                if (directory != null)
                {
                    Directory.CreateDirectory(directory);
                }

                // Save default settings
                File.WriteAllText(
                    settingsPath, 
                    JsonSerializer.Serialize(defaultSettings, new JsonSerializerOptions { WriteIndented = true })
                );

                return defaultSettings;
            }

            // Read and parse existing settings file
            try 
            {
                string jsonContent = File.ReadAllText(settingsPath);
                var settings = JsonSerializer.Deserialize<AppSettings>(jsonContent) ?? new AppSettings();

                // Ensure we have a valid settings object
                return settings ?? new AppSettings();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error reading settings file: {ex.Message}");
                return new AppSettings();
            }
        }

        // Method to save settings
        public void SaveSettings(string settingsPath = null)
        {
            settingsPath ??= Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "appsettings.json");

            try 
            {
                File.WriteAllText(
                    settingsPath, 
                    JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true })
                );
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error saving settings file: {ex.Message}");
            }
        }
    }

    public class AuthToken
    {
        public int Id { get; set; }
        public string Username { get; set; } = string.Empty;
        public string TokenHash { get; set; } = string.Empty;
        public string TokenSalt { get; set; } = string.Empty;
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    }

    public class AuthDbContext : DbContext
    {
        public AuthDbContext(DbContextOptions<AuthDbContext> options) : base(options) { }

        public DbSet<AuthToken> Tokens { get; set; }

        // This method checks if the database was properly created by the main app
        public bool IsValidDatabase()
        {
            try
            {
                // Try to query the Tokens table - this will throw if the table doesn't exist
                return Tokens.Any();
            }
            catch (Exception)
            {
                return false;
            }
        }
    }

    public class TokenService
    {
        private readonly AuthDbContext _dbContext;
        private readonly string _tokenFolderPath;

        public TokenService(AuthDbContext dbContext)
        {
            _dbContext = dbContext;
            _tokenFolderPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "tokens");
            
            if (!Directory.Exists(_tokenFolderPath))
            {
                Directory.CreateDirectory(_tokenFolderPath);
            }
        }
        
        public async Task<string> GenerateTokenAsync(string username)
        {
            string token = Guid.NewGuid().ToString();
            
            byte[] salt = GenerateSalt();
            string saltString = Convert.ToBase64String(salt);
            
            string hashedToken = HashToken(token, salt);
            
            var tokenEntry = new AuthToken
            {
                Username = username,
                TokenHash = hashedToken,
                TokenSalt = saltString,
                CreatedAt = DateTime.UtcNow
            };
            
            _dbContext.Tokens.Add(tokenEntry);
            await _dbContext.SaveChangesAsync();
            
            await SaveTokenToFileAsync(username, token);
            
            return token;
        }

        public async Task<List<AuthToken>> GetAllTokensAsync()
        {
            return await _dbContext.Tokens.ToListAsync();
        }

        public async Task<bool> RevokeTokenAsync(int id)
        {
            var token = await _dbContext.Tokens.FindAsync(id);
            if (token == null)
            {
                return false;
            }

            // Delete the token file if it exists
            string filePath = Path.Combine(_tokenFolderPath, $"{token.Username}.txt");
            if (File.Exists(filePath))
            {
                File.Delete(filePath);
                Log.Information("Deleted token file for {Username}", token.Username);
            }

            // Remove from database
            _dbContext.Tokens.Remove(token);
            await _dbContext.SaveChangesAsync();
            return true;
        }
        
        private string HashToken(string token, byte[] salt)
        {
            using (var pbkdf2 = new Rfc2898DeriveBytes(token, salt, 10000, HashAlgorithmName.SHA256))
            {
                byte[] hash = pbkdf2.GetBytes(32);
                return Convert.ToBase64String(hash);
            }
        }
        
        private byte[] GenerateSalt()
        {
            byte[] salt = new byte[16];
            using (var rng = RandomNumberGenerator.Create())
            {
                rng.GetBytes(salt);
            }
            return salt;
        }
        
        private async Task SaveTokenToFileAsync(string username, string token)
        {
            try
            {
                string filePath = Path.Combine(_tokenFolderPath, $"{username}.txt");
                await File.WriteAllTextAsync(filePath, token);
                Log.Information("Token file saved to {FilePath}", filePath);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to save token file for user {Username}", username);
                throw;
            }
        }
    }

    class Program
    {
        static async Task Main(string[] args)
        {
            // Configure logging
            Log.Logger = new LoggerConfiguration()
                .WriteTo.Console()
                .CreateLogger();

            Log.Information("Starting Token Generator...");

            try
            {
                var serviceProvider = ConfigureServices();
                
                using (var scope = serviceProvider.CreateScope())
                {
                    var dbContext = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
                    var appSettings = scope.ServiceProvider.GetRequiredService<AppSettings>();
                    
                    var dbPath = appSettings.GetResolvedDatabasePath();
                    if (!File.Exists(dbPath))
                    {
                        DisplayErrorAndExit("Database not found. Please run the main application first.");
                        return;
                    }
                    
                    if (!dbContext.IsValidDatabase())
                    {
                        DisplayErrorAndExit("Invalid database structure. Please run the main application first.");
                        return;
                    }
                    
                    if (!dbContext.Tokens.Any())
                    {
                        DisplayErrorAndExit("No tokens found in database. Please run the main application first to create an initial token.");
                        return;
                    }
                }

                if (args.Length > 0 && !string.IsNullOrWhiteSpace(args[0]))
                {
                    await GenerateTokenForUserAsync(args[0], serviceProvider);
                    return;
                }

                bool exitRequested = false;

                while (!exitRequested)
                {
                    DisplayMenu();
                    string choice = Console.ReadLine() ?? "";

                    switch (choice)
                    {
                        case "1":
                            await ListAllTokensAsync(serviceProvider);
                            break;
                        case "2":
                            await AddNewTokenAsync(serviceProvider);
                            break;
                        case "3":
                            await RevokeTokenAsync(serviceProvider);
                            break;
                        case "4":
                            UpdateDatabasePath(serviceProvider);
                            break;
                        case "0":
                            exitRequested = true;
                            break;
                        default:
                            Console.WriteLine("Invalid option. Please try again.");
                            break;
                    }

                    if (!exitRequested)
                    {
                        Console.WriteLine("\nPress any key to return to menu...");
                        Console.ReadKey();
                        Console.Clear();
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "An error occurred: {ErrorMessage}", ex.Message);
                DisplayErrorAndExit($"An error occurred: {ex.Message}");
            }
        }

        static void DisplayErrorAndExit(string errorMessage)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine("\nERROR: " + errorMessage);
            Console.ResetColor();
            Console.WriteLine("\nPress any key to exit...");
            Console.ReadKey();
            Environment.Exit(1);
        }

        static void DisplayMenu()
        {
            Console.WriteLine("===============================================");
            Console.WriteLine("      MinimalSQLReader Token Generator        ");
            Console.WriteLine("===============================================");
            Console.WriteLine("1. List all existing tokens");
            Console.WriteLine("2. Generate new token");
            Console.WriteLine("3. Revoke token");
            Console.WriteLine("4. Update Database Path");
            Console.WriteLine("0. Exit");
            Console.WriteLine("-----------------------------------------------");
            Console.Write("Select an option: ");
        }

        static async Task ListAllTokensAsync(IServiceProvider serviceProvider)
        {
            using var scope = serviceProvider.CreateScope();
            var tokenService = scope.ServiceProvider.GetRequiredService<TokenService>();

            var tokens = await tokenService.GetAllTokensAsync();

            if (tokens.Count == 0)
            {
                Console.WriteLine("\nNo tokens found in the database.");
                return;
            }

            Console.WriteLine("\n=== Existing Tokens ===");
            Console.WriteLine($"{"ID",-5} {"Username",-20} {"Created",-20} {"Token File",-15}");
            Console.WriteLine(new string('-', 60));

            foreach (var token in tokens)
            {
                string tokenFilePath = Path.Combine(Directory.GetCurrentDirectory(), "tokens", $"{token.Username}.txt");
                string tokenFileStatus = File.Exists(tokenFilePath) ? "Available" : "Missing";

                Console.WriteLine($"{token.Id,-5} {token.Username,-20} {token.CreatedAt.ToString("yyyy-MM-dd HH:mm"),-20} {tokenFileStatus,-15}");
            }
        }

        static async Task AddNewTokenAsync(IServiceProvider serviceProvider)
        {
            Console.WriteLine("\n=== Generate New Token ===");
            Console.Write("Enter username (leave blank for machine name): ");
            string? input = Console.ReadLine();
            string username = string.IsNullOrWhiteSpace(input) ? Environment.MachineName : input;

            using var scope = serviceProvider.CreateScope();
            var tokenService = scope.ServiceProvider.GetRequiredService<TokenService>();

            try
            {
                Console.WriteLine($"Generating token for user: {username}");
                var token = await tokenService.GenerateTokenAsync(username);

                Console.WriteLine("\n--- Token Generated Successfully ---");
                Console.WriteLine($"Username: {username}");
                Console.WriteLine($"Token: {token}");
                Console.WriteLine($"Token file: {Path.Combine(Directory.GetCurrentDirectory(), "tokens", $"{username}.txt")}");
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error generating token: {ErrorMessage}", ex.Message);
                Console.WriteLine($"\nError generating token: {ex.Message}");
            }
        }

        static async Task RevokeTokenAsync(IServiceProvider serviceProvider)
        {
            // First list all tokens
            await ListAllTokensAsync(serviceProvider);

            Console.WriteLine("\n=== Revoke Token ===");
            Console.Write("Enter token ID to revoke (or 0 to cancel): ");
            
            if (!int.TryParse(Console.ReadLine(), out int tokenId) || tokenId <= 0)
            {
                Console.WriteLine("Operation cancelled.");
                return;
            }

            using var scope = serviceProvider.CreateScope();
            var tokenService = scope.ServiceProvider.GetRequiredService<TokenService>();

            bool result = await tokenService.RevokeTokenAsync(tokenId);
            if (result)
            {
                Console.WriteLine($"Token with ID {tokenId} has been revoked successfully.");
            }
            else
            {
                Console.WriteLine($"Token with ID {tokenId} not found.");
            }
        }

        static async Task GenerateTokenForUserAsync(string username, IServiceProvider serviceProvider)
        {
            Log.Information("Token Generator - Automated Mode");
            Log.Information("=================================");

            try
            {                
                using var scope = serviceProvider.CreateScope();
                var tokenService = scope.ServiceProvider.GetRequiredService<TokenService>();

                Log.Information("Generating token for user: {Username}", username);
                var token = await tokenService.GenerateTokenAsync(username);
                
                Log.Information("Token generation successful!");
                Log.Information("Username: {Username}", username);
                Log.Information("Token: {Token}", token);
                Log.Information("Token file: {FilePath}", Path.Combine(Directory.GetCurrentDirectory(), "tokens", $"{username}.txt"));
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error generating token: {ErrorMessage}", ex.Message);
                DisplayErrorAndExit($"Error generating token: {ex.Message}");
            }
        }

        static void UpdateDatabasePath(IServiceProvider serviceProvider)
        {
            Console.WriteLine("\n=== Update Database Path ===");
            Console.WriteLine("Enter new database path:");
            Console.WriteLine("- Full absolute path (e.g., C:\\path\\to\\auth.db)");
            Console.WriteLine("- Relative path (e.g., ..\\..\\auth.db)");
            Console.Write("Path: ");
            string newPath = Console.ReadLine();

            if (string.IsNullOrWhiteSpace(newPath))
            {
                Console.WriteLine("Path update cancelled.");
                return;
            }

            // Retrieve the app settings
            var appSettings = serviceProvider.GetRequiredService<AppSettings>();
            
            // Update and save the path
            appSettings.DatabasePath = newPath;
            appSettings.SaveSettings();

            Console.WriteLine($"Database path updated to: {newPath}");
            Console.WriteLine($"Resolved full path: {appSettings.GetResolvedDatabasePath()}");
            Console.WriteLine("Restart the application for changes to take effect.");
        }

        static IServiceProvider ConfigureServices()
        {
            // Load settings
            var appSettings = AppSettings.LoadSettings();
            
            var services = new ServiceCollection();
            
            // Use the resolved database path
            services.AddDbContext<AuthDbContext>(options =>
                options.UseSqlite($"Data Source={appSettings.GetResolvedDatabasePath()}"));

            services.AddSingleton(appSettings);
            services.AddScoped<TokenService>();
            
            return services.BuildServiceProvider();
        }
    }
}