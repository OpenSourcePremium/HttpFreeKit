using System.Globalization;
using System.Text;
using System.Text.Json;

namespace HttpFreeKit.Core;

public enum RequestCodeFormat
{
    Curl,
    Batch,
    NodeJs,
    Python,
    Lua,
    Go,
    CSharp,
    Cpp
}

public static class RequestCodeGenerator
{
    private static readonly HashSet<string> ExcludedHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Host", "Content-Length", "Connection", "Proxy-Connection", "Transfer-Encoding",
        "Expect", "Accept-Encoding"
    };

    public static string Generate(TrafficEntry entry, RequestCodeFormat format)
    {
        string method = entry.Method.Trim().ToUpperInvariant();
        string url = entry.Url;
        var headers = ParseHeaders(entry.RequestHeaders)
            .Where(header => !ExcludedHeaders.Contains(header.Name))
            .ToList();
        if (!string.IsNullOrWhiteSpace(entry.RequestContentType)
            && headers.All(header => !header.Name.Equals("Content-Type", StringComparison.OrdinalIgnoreCase)))
            headers.Add(("Content-Type", entry.RequestContentType));

        byte[] body = GetBody(entry);
        bool hasBody = body.Length > 0;
        string incompleteNote = IsBodyIncomplete(entry, body) ? format switch
        {
            RequestCodeFormat.Curl => "\n# Warning: only the captured body preview is available.",
            RequestCodeFormat.Batch => "\nrem Warning: only the captured body preview is available.",
            RequestCodeFormat.Python => "\n# Warning: only the captured body preview is available.",
            RequestCodeFormat.Lua => "\n-- Warning: only the captured body preview is available.",
            _ => "\n// Warning: only the captured body preview is available."
        } : "";

        return format switch
        {
            RequestCodeFormat.Curl => GenerateCurl(method, url, headers, body, hasBody, incompleteNote),
            RequestCodeFormat.Batch => GenerateBatch(method, url, headers, body, hasBody, incompleteNote),
            RequestCodeFormat.NodeJs => GenerateNodeJs(method, url, headers, body, hasBody, incompleteNote),
            RequestCodeFormat.Python => GeneratePython(method, url, headers, body, hasBody, incompleteNote),
            RequestCodeFormat.Lua => GenerateLua(method, url, headers, body, hasBody, incompleteNote),
            RequestCodeFormat.Go => GenerateGo(method, url, headers, body, hasBody, incompleteNote),
            RequestCodeFormat.CSharp => GenerateCSharp(method, url, headers, body, hasBody, incompleteNote),
            RequestCodeFormat.Cpp => GenerateCpp(method, url, headers, body, hasBody, incompleteNote),
            _ => throw new ArgumentOutOfRangeException(nameof(format))
        };
    }

    private static List<(string Name, string Value)> ParseHeaders(string text)
    {
        var result = new List<(string Name, string Value)>();
        foreach (string line in text.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries))
        {
            int colon = line.IndexOf(':');
            if (colon <= 0) continue;
            string name = line[..colon].Trim();
            if (name.StartsWith(':')) continue;
            result.Add((name, line[(colon + 1)..].Trim()));
        }
        return result;
    }

    private static byte[] GetBody(TrafficEntry entry)
    {
        if (entry.RequestReplayBody is { } replayBody)
            return replayBody;
        if (entry.RequestIsBinary)
            return entry.RequestBodyBytes ?? entry.RequestPayloadPreview ?? Array.Empty<byte>();
        if (!string.IsNullOrEmpty(entry.RequestBody))
            return Encoding.UTF8.GetBytes(entry.RequestBody);
        return entry.RequestPayloadPreview ?? Array.Empty<byte>();
    }

    private static bool IsBodyIncomplete(TrafficEntry entry, byte[] body)
        => entry.RequestBodyIncomplete
           || entry.RequestPayloadLength.HasValue && entry.RequestPayloadLength.Value > body.LongLength
           || entry.RequestPreviewUnavailable;

    private static string GenerateCurl(string method, string url, IReadOnlyList<(string Name, string Value)> headers,
        byte[] body, bool hasBody, string note)
    {
        static string Quote(string value) => "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";

        var command = new StringBuilder();
        if (hasBody)
            command.Append("printf '%s' ").Append(Quote(Convert.ToBase64String(body)))
                .Append(" | base64 --decode | \\\n");
        command.Append("curl --request ").Append(Quote(method)).Append(" --url ").Append(Quote(url));
        foreach (var header in headers)
            command.Append(" \\\n  --header ").Append(Quote($"{header.Name}: {header.Value}"));
        if (hasBody) command.Append(" \\\n  --data-binary @-");
        return command.Append(note).ToString();
    }

    private static string GenerateBatch(string method, string url, IReadOnlyList<(string Name, string Value)> headers,
        byte[] body, bool hasBody, string note)
    {
        static string Quote(string value)
        {
            string escaped = value.Replace("%", "%%", StringComparison.Ordinal)
                .Replace("\"", "\\\"", StringComparison.Ordinal);
            return "\"" + escaped + "\"";
        }

        var code = new StringBuilder("@echo off\r\nsetlocal DisableDelayedExpansion\r\n");
        if (hasBody)
        {
            string tempPrefix = "%TEMP%\\httpfreekit-request-body";
            string encodedPath = tempPrefix + ".b64";
            string bodyPath = tempPrefix + ".bin";
            string base64 = Convert.ToBase64String(body);
            code.Append("\r\nset \"BODY_B64=").Append(encodedPath).Append("\"\r\n")
                .Append("set \"BODY_FILE=").Append(bodyPath).Append("\"\r\n");
            for (int offset = 0; offset < base64.Length; offset += 76)
            {
                string redirection = offset == 0 ? ">" : ">>";
                code.Append("echo ").Append(base64.Substring(offset, Math.Min(76, base64.Length - offset)))
                    .Append(' ').Append(redirection).Append(" \"%BODY_B64%\"\r\n");
            }
            code.Append("certutil -f -decode \"%BODY_B64%\" \"%BODY_FILE%\" >nul\r\n")
                .Append("del \"%BODY_B64%\"\r\n")
                .Append("curl.exe --request ").Append(Quote(method)).Append(" --url ").Append(Quote(url));
            foreach (var header in headers)
                code.Append(" ^\r\n  --header ").Append(Quote($"{header.Name}: {header.Value}"));
            code.Append(" ^\r\n  --data-binary \"@%BODY_FILE%\"\r\n")
                .Append("del \"%BODY_FILE%\"");
        }
        else
        {
            code.Append("curl.exe --request ").Append(Quote(method)).Append(" --url ").Append(Quote(url));
            foreach (var header in headers)
                code.Append(" ^\r\n  --header ").Append(Quote($"{header.Name}: {header.Value}"));
        }
        if (note.Length > 0)
            code.Append(note);
        return code.ToString();
    }

    private static string GenerateNodeJs(string method, string url, IReadOnlyList<(string Name, string Value)> headers,
        byte[] body, bool hasBody, string note)
    {
        var code = new StringBuilder("const headers = new Headers();\n");
        foreach (var header in headers)
            code.Append("headers.append(").Append(Json(header.Name)).Append(", ")
                .Append(Json(header.Value)).Append(");\n");
        code.Append("\nconst response = await fetch(").Append(Json(url)).Append(", {\n")
            .Append("  method: ").Append(Json(method)).Append(",\n")
            .Append("  headers");
        if (hasBody && method is not ("GET" or "HEAD"))
            code.Append(",\n  body: Buffer.from(").Append(Json(Convert.ToBase64String(body))).Append(", 'base64')");
        code.Append("\n});\nconsole.log(response.status, await response.text());");
        if (hasBody && method is "GET" or "HEAD")
            code.Append("\n// Fetch does not permit a request body with ").Append(method).Append('.');
        return code.Append(note).ToString();
    }

    private static string GeneratePython(string method, string url, IReadOnlyList<(string Name, string Value)> headers,
        byte[] body, bool hasBody, string note)
    {
        var code = new StringBuilder("import base64\nimport requests\n\n")
            .Append("url = ").Append(Py(url)).Append("\n")
            .Append("headers = [\n");
        foreach (var header in headers)
            code.Append("    (").Append(Py(header.Name)).Append(", ").Append(Py(header.Value)).Append("),\n");
        code.Append("]\n");
        if (hasBody)
            code.Append("body = base64.b64decode(").Append(Py(Convert.ToBase64String(body))).Append(")\n");
        code.Append("\nresponse = requests.request(").Append(Py(method)).Append(", url, headers=headers");
        if (hasBody) code.Append(", data=body");
        code.Append(")\nprint(response.status_code, response.text)");
        return code.Append(note).ToString();
    }

    private static string GenerateLua(string method, string url, IReadOnlyList<(string Name, string Value)> headers,
        byte[] body, bool hasBody, string note)
    {
        var code = new StringBuilder("local http = require(\"socket.http\")\n")
            .Append("local ltn12 = require(\"ltn12\")\n\n")
            .Append("local response = {}\n")
            ;
        if (hasBody)
            code.Append("local mime = require(\"mime\")\n")
                .Append("local body = mime.unb64(").Append(Lua(Convert.ToBase64String(body))).Append(")\n");
        code.Append("local request = {\n")
            .Append("  url = ").Append(Lua(url)).Append(",\n")
            .Append("  method = ").Append(Lua(method)).Append(",\n")
            .Append("  headers = {\n");
        foreach (var header in headers)
            code.Append("    [").Append(Lua(header.Name)).Append("] = ")
                .Append(Lua(header.Value)).Append(",\n");
        if (hasBody)
            code.Append("    [\"Content-Length\"] = tostring(#body),\n");
        code.Append("  },\n");
        if (hasBody)
            code.Append("  source = ltn12.source.string(body),\n");
        code.Append("  sink = ltn12.sink.table(response),\n")
            .Append("}\nlocal _, status = http.request(request)\n")
            .Append("print(status, table.concat(response))");
        return code.Append(note).ToString();
    }

    private static string GenerateGo(string method, string url, IReadOnlyList<(string Name, string Value)> headers,
        byte[] body, bool hasBody, string note)
    {
        var code = new StringBuilder();
        code.Append("package main\n\nimport (\n\t\"fmt\"\n\t\"io\"\n\t\"net/http\"\n");
        if (hasBody)
            code.Append("\t\"encoding/base64\"\n\t\"strings\"\n");
        code.Append(")\n\n")
            .Append("func main() {\n\trequest");
        if (hasBody)
            code.Append("Body, decodeErr := base64.StdEncoding.DecodeString(")
                .Append(Go(Convert.ToBase64String(body))).Append(")\n")
                .Append("\tif decodeErr != nil { panic(decodeErr) }\n")
                .Append("\trequest, err := http.NewRequest(").Append(Go(method)).Append(", ")
                .Append(Go(url)).Append(", strings.NewReader(string(requestBody)))\n");
        else
            code.Append(", err := http.NewRequest(").Append(Go(method)).Append(", ")
                .Append(Go(url)).Append(", nil)\n");
        code.Append("\tif err != nil { panic(err) }\n");
        foreach (var header in headers)
            code.Append("\trequest.Header.Add(").Append(Go(header.Name)).Append(", ")
                .Append(Go(header.Value)).Append(")\n");
        code.Append("\tresponse, err := http.DefaultClient.Do(request)\n")
            .Append("\tif err != nil { panic(err) }\n\tdefer response.Body.Close()\n")
            .Append("\tdata, err := io.ReadAll(response.Body)\n")
            .Append("\tif err != nil { panic(err) }\n\tfmt.Println(response.Status, string(data))\n}");
        return code.Append(note).ToString();
    }

    private static string GenerateCSharp(string method, string url,
        IReadOnlyList<(string Name, string Value)> headers, byte[] body, bool hasBody, string note)
    {
        var code = new StringBuilder("using System;\nusing System.Net.Http;\nusing System.Threading.Tasks;\n\n")
            .Append("using var client = new HttpClient();\n")
            .Append("using var request = new HttpRequestMessage(new HttpMethod(").Append(Json(method))
            .Append("), ").Append(Json(url)).Append(");\n");
        if (hasBody)
            code.Append("request.Content = new ByteArrayContent(Convert.FromBase64String(")
                .Append(Json(Convert.ToBase64String(body))).Append("));\n");
        foreach (var header in headers)
        {
            if (header.Name.StartsWith("Content-", StringComparison.OrdinalIgnoreCase))
            {
                if (!hasBody)
                    code.Append("request.Content ??= new ByteArrayContent(Array.Empty<byte>());\n");
                code.Append("request.Content.Headers.TryAddWithoutValidation(");
            }
            else
                code.Append("request.Headers.TryAddWithoutValidation(");
            code.Append(Json(header.Name)).Append(", ").Append(Json(header.Value)).Append(");\n");
        }
        code.Append("\nusing var response = await client.SendAsync(request);\n")
            .Append("Console.WriteLine($\"{(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}\");");
        return code.Append(note).ToString();
    }

    private static string GenerateCpp(string method, string url,
        IReadOnlyList<(string Name, string Value)> headers, byte[] body, bool hasBody, string note)
    {
        var code = new StringBuilder("#include <curl/curl.h>\n#include <cstdint>\n#include <vector>\n\n")
            .Append("int main() {\n    CURL* curl = curl_easy_init();\n    if (!curl) return 1;\n")
            .Append("    curl_easy_setopt(curl, CURLOPT_URL, ").Append(Cpp(url)).Append(");\n")
            .Append("    curl_easy_setopt(curl, CURLOPT_CUSTOMREQUEST, ").Append(Cpp(method)).Append(");\n")
            .Append("    curl_slist* headers = nullptr;\n");
        foreach (var header in headers)
            code.Append("    headers = curl_slist_append(headers, ")
                .Append(Cpp($"{header.Name}: {header.Value}")).Append(");\n");
        if (headers.Count > 0)
            code.Append("    curl_easy_setopt(curl, CURLOPT_HTTPHEADER, headers);\n");
        if (hasBody)
            code.Append("    const std::vector<std::uint8_t> body = {")
                .Append(string.Join(", ", body.Select(value => "0x" + value.ToString("X2", CultureInfo.InvariantCulture))))
                .Append("};\n    curl_easy_setopt(curl, CURLOPT_POSTFIELDS, body.data());\n")
                .Append("    curl_easy_setopt(curl, CURLOPT_POSTFIELDSIZE_LARGE, static_cast<curl_off_t>(body.size()));\n");
        code.Append("    CURLcode result = curl_easy_perform(curl);\n")
            .Append("    if (headers) curl_slist_free_all(headers);\n")
            .Append("    curl_easy_cleanup(curl);\n    return result == CURLE_OK ? 0 : 1;\n}");
        return code.Append(note).ToString();
    }

    private static string Json(string value) => JsonSerializer.Serialize(value);
    private static string Py(string value) => Json(value);
    private static string Lua(string value)
        => "\"" + value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal)
            .Replace("\r", "\\r", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal) + "\"";
    private static string Go(string value) => Json(value);

    private static string Cpp(string value)
    {
        var literal = new StringBuilder("\"");
        foreach (char character in value)
        {
            literal.Append(character switch
            {
                '\\' => "\\\\",
                '"' => "\\\"",
                '\r' => "\\r",
                '\n' => "\\n",
                '\t' => "\\t",
                _ when char.IsControl(character) => "\\x" + ((int)character).ToString("X2", CultureInfo.InvariantCulture) + "\"\"",
                _ => character.ToString()
            });
        }
        return literal.Append('"').ToString();
    }
}
