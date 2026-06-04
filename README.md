# QQWRY-EN

将纯真 IP 数据库（QQWRY）从中文翻译为英文的工具。

## 功能特点

- 使用 DeepSeek V4 Flash API 翻译地点（国家、省、市、区）四元组
- 使用 DeepSeek V4 Flash API 翻译 ISP 名称
- 使用 Google 翻译翻译其他字段
- 支持并发翻译，可配置并发数
- 支持缓存，避免重复翻译
- 所有配置均可通过 `appsettings.json` 调整
- GitHub Actions 自动更新（每月1日、15日）

## 下载

从 [Releases](https://github.com/YOUR_USERNAME/QQWRY-EN/releases) 页面下载最新的 `qqwry_en.ipdb` 或 `output_en.txt` 文件。

## 自动构建

本项目使用 GitHub Actions 自动构建，每月1日和15日自动更新：

1. 从 [sjzar/ips](https://github.com/sjzar/ips) 获取最新 ips 工具
2. 从 [nmgliangwei/qqwry.ipdb](https://github.com/nmgliangwei/qqwry.ipdb/releases/) 获取最新 qqwry.ipdb
3. 解包、翻译、重新打包
4. 发布到 Releases（保留最近3个版本）

### 配置 Secrets

在仓库 Settings > Secrets and variables > Actions 中添加：

| Secret | 说明 |
|--------|------|
| DEEPSEEK_API_KEY | DeepSeek API Key |

## 手动构建

### 1. 安装依赖

确保已安装 [.NET 9.0 SDK](https://dotnet.microsoft.com/download/dotnet/9.0)。

### 2. 使用 ips 工具导出 QQWRY 数据

使用 [sjzar/ips](https://github.com/sjzar/ips) 工具将 QQWRY 数据库导出为 TXT 格式：

```bash
# 安装 ips 工具
go install github.com/sjzar/ips@latest

# 导出 QQWRY 数据为 TXT 格式
ips dump -i qqwry.ipdb -o qqwry.txt
```

导出的 `qqwry.txt` 格式如下：

```
# ip_cidr	country_name,region_name,city_name,district_name,owner_domain,isp_domain,country_code,continent_code
0.0.0.0/8	IANA保留地址,,,,,,,ZZ
1.0.0.0/24	澳大利亚,,,,,,,AU
```

### 3. 配置 DeepSeek API

编辑 `appsettings.json`，填入你的 DeepSeek API Key：

```json
{
  "DeepSeek": {
    "ApiKey": "your-api-key-here",
    "BaseUrl": "https://api.deepseek.com",
    "Model": "deepseek-v4-flash"
  }
}
```

或者设置环境变量 `DEEPSEEK_API_KEY`：

```bash
# Windows PowerShell
$env:DEEPSEEK_API_KEY="your-api-key-here"

# Linux/macOS
export DEEPSEEK_API_KEY="your-api-key-here"
```

> 当配置文件中的 API Key 为空或为默认值 `your-api-key-here` 时，程序会自动从环境变量 `DEEPSEEK_API_KEY` 读取。

### 4. 运行翻译

```bash
dotnet run
```

翻译完成后，输出文件为 `output_en.txt`。

### 5. 重新打包为 QQWRY 格式

使用 ips 工具将翻译后的数据重新打包（请复制原文件头部的注释内容到新文件中）：

```bash
ips pack -i output_en.txt -o qqwry_en.ipdb
```

## 配置说明

所有配置项在 `appsettings.json` 中：

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

| 配置项 | 说明 | 默认值 |
|--------|------|--------|
| DeepSeek.ApiKey | DeepSeek API Key | - |
| DeepSeek.BaseUrl | DeepSeek API 地址 | https://api.deepseek.com |
| DeepSeek.Model | 使用的模型 | deepseek-v4-flash |
| Concurrency.DeepSeekConcurrency | DeepSeek 并发数 | 64 |
| Concurrency.GoogleConcurrency | Google 翻译并发数 | 10 |
| Files.InputFile | 输入文件 | qqwry.txt |
| Files.OutputFile | 输出文件 | output_en.txt |
| Files.LocationCacheFile | 地点缓存文件 | location_cache.tsv |
| Files.IspCacheFile | ISP 缓存文件 | isp_cache.tsv |
| Files.GeneralCacheFile | 通用缓存文件 | translation_cache.tsv |
| Prompts.LocationPrompt | 地点翻译提示词 | - |
| Prompts.IspPrompt | ISP 翻译提示词 | - |

## 缓存机制

程序使用三个缓存文件避免重复翻译：

- `location_cache.tsv`：地点四元组（国家|省|市|区）缓存
- `isp_cache.tsv`：ISP 名称缓存
- `translation_cache.tsv`：其他字段（所有者域名）缓存

缓存格式为 TSV（Tab 分隔），每行格式：`原文\t译文`

## 翻译规则

### 地点翻译

- 使用 DeepSeek API 翻译
- 四元组（国家、省、市、区）合并缓存
- 不输出 Province、City、County、District 等后缀

### ISP 翻译

- 使用 DeepSeek API 翻译
- 优先按中国语境翻译
- 含有额外信息的 ISP 使用 `_` 分隔符
- 格式：`ISP_类型_所有者` 或 `ISP_额外信息`

### 其他字段

- 使用 Google 翻译
- 包括所有者域等字段

## 项目结构

```
QQWRY-EN/
├── Program.cs              # 主程序
├── QQWRY-EN.csproj         # 项目文件
├── appsettings.json        # 配置文件
├── qqwry.txt               # 输入文件（需自行导出）
├── output_en.txt           # 输出文件
├── location_cache.tsv      # 地点缓存
├── isp_cache.tsv           # ISP 缓存
└── translation_cache.tsv   # 通用缓存
```

## 致谢

- [sjzar/ips](https://github.com/sjzar/ips) - IP 数据库工具
- [纯真网络](http://www.cz88.net/) - QQWRY 数据库
