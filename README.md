# QQWRY-EN

A tool to translate the Chunzhen IP Database (CZ88 QQWRY) from Chinese to English.

## Features

- Translates location quads (Country, Region, City, District) using DeepSeek V4 Flash API
- Translates ISP names using DeepSeek V4 Flash API
- Translates other fields using Google Translate
- Configurable concurrent translation with caching
- All settings configurable via `appsettings.json`
- Auto-updates via GitHub Actions (1st and 15th of each month)

## Download

Download the latest `qqwry_en.ipdb` or `output_en.txt` from the [Releases](https://github.com/mili-tan/ArashiDNS.QqwryEN/releases) page.

## Automated Build

This project uses GitHub Actions for automated builds, updating on the 1st and 15th of each month:

1. Fetches the latest ips tool from [sjzar/ips](https://github.com/sjzar/ips)
2. Fetches the latest qqwry 
3. Unpacks, translates, and repacks
4. Publishes to Releases (keeps latest 3 versions)

### Configure Secrets

Add the following in Settings > Secrets and variables > Actions:

| Secret | Description |
|--------|-------------|
| DEEPSEEK_API_KEY | DeepSeek API Key |

## Manual Build

### 1. Install Dependencies

Ensure [.NET 9.0 SDK](https://dotnet.microsoft.com/download/dotnet/9.0) is installed.

### 2. Export QQWRY Data with ips

Use [sjzar/ips](https://github.com/sjzar/ips) to export the QQWRY database to TXT format:

```bash
# Install ips
go install github.com/sjzar/ips@latest

# Export QQWRY data to TXT
ips dump -i qqwry.ipdb -o qqwry.txt
```

Exported `qqwry.txt` format:

```
# ip_cidr	country_name,region_name,city_name,district_name,owner_domain,isp_domain,country_code,continent_code
0.0.0.0/8	IANA保留地址,,,,,,,ZZ
1.0.0.0/24	澳大利亚,,,,,,,AU
```

### 3. Configure DeepSeek API

Edit `appsettings.json` with your DeepSeek API Key:

```json
{
  "DeepSeek": {
    "ApiKey": "your-api-key-here",
    "BaseUrl": "https://api.deepseek.com",
    "Model": "deepseek-v4-flash"
  }
}
```

Or set the environment variable `DEEPSEEK_API_KEY`:

```bash
# Windows PowerShell
$env:DEEPSEEK_API_KEY="your-api-key-here"

# Linux/macOS
export DEEPSEEK_API_KEY="your-api-key-here"
```

> When the API Key in the config file is empty or set to the default `your-api-key-here`, the program will automatically read from the `DEEPSEEK_API_KEY` environment variable.

### 4. Run Translation

```bash
dotnet run
```

Output file: `output_en.txt`

### 5. Repack to QQWRY Format

Use ips to repack the translated data:

```bash
ips pack -i output_en.txt -o qqwry_en.ipdb
```

## Configuration

All settings are in `appsettings.json`:

```json
{
  "DeepSeek": {
    "ApiKey": "your-api-key-here",
    "BaseUrl": "https://api.deepseek.com",
    "Model": "deepseek-v4-flash"
  },
  "Concurrency": {
    "DeepSeekConcurrency": 64,
    "GoogleConcurrency": 10
  },
  "Files": {
    "InputFile": "qqwry.txt",
    "OutputFile": "output_en.txt",
    "LocationCacheFile": "location_cache.tsv",
    "IspCacheFile": "isp_cache.tsv",
    "GeneralCacheFile": "translation_cache.tsv"
  },
  "Prompts": {
    "LocationPrompt": "...",
    "IspPrompt": "..."
  }
}
```

| Setting | Description | Default |
|---------|-------------|---------|
| DeepSeek.ApiKey | DeepSeek API Key | - |
| DeepSeek.BaseUrl | DeepSeek API endpoint | https://api.deepseek.com |
| DeepSeek.Model | Model name | deepseek-v4-flash |
| Concurrency.DeepSeekConcurrency | DeepSeek concurrency | 64 |
| Concurrency.GoogleConcurrency | Google Translate concurrency | 10 |
| Files.InputFile | Input file | qqwry.txt |
| Files.OutputFile | Output file | output_en.txt |
| Files.LocationCacheFile | Location cache file | location_cache.tsv |
| Files.IspCacheFile | ISP cache file | isp_cache.tsv |
| Files.GeneralCacheFile | General cache file | translation_cache.tsv |
| Prompts.LocationPrompt | Location translation prompt | (built-in) |
| Prompts.IspPrompt | ISP translation prompt | (built-in) |

## Caching

Three cache files are used to avoid redundant translations:

- `location_cache.tsv`: Location quads (Country|Region|City|District)
- `isp_cache.tsv`: ISP names
- `translation_cache.tsv`: Other fields (owner domains)

Cache format is TSV (tab-separated), one entry per line: `source\ttranslation`

## Translation Rules

### Location Translation

- Uses DeepSeek API
- Location quads (Country, Region, City, District) are cached as a combined key
- No suffixes like Province, City, County, District are appended

### ISP Translation

- Uses DeepSeek API
- Prioritizes Chinese context by default
- ISP names with additional info use `_` as separator
- Format: `ISP_Type_Owner` or `ISP_ExtraInfo`

### Other Fields

- Uses Google Translate
- Includes owner domain fields

## Project Structure

```
QQWRY-EN/
├── Program.cs              # Main program
├── QQWRY-EN.csproj         # Project file
├── appsettings.json        # Configuration
├── qqwry.txt               # Input file (user-provided)
├── output_en.txt           # Output file
├── location_cache.tsv      # Location cache
├── isp_cache.tsv           # ISP cache
└── translation_cache.tsv   # General cache
```

## Credits

- [sjzar/ips](https://github.com/sjzar/ips) - IP database tool
- [Chunzhen Network](http://www.cz88.net/) - QQWRY database
