using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using UnityEngine;

/// <summary>
/// Helper to build Cloudflare R2 URLs using values from env.json.
/// Generates SigV4 presigned URLs when credentials are available,
/// otherwise falls back to the public base URL or the raw input.
/// </summary>
public static class CloudflareR2UrlBuilder
{
    private const string Region = "auto";
    private const int DefaultExpirySeconds = 3600;
    private static R2EnvConfig cachedConfig;
    private static bool attemptedLoad;

    [Serializable]
    private class R2EnvConfig
    {
        public string R2_ACCESS_KEY;
        public string R2_SECRET_KEY;
        public string R2_BUCKET;
        public string R2_ENDPOINT;
        public string R2_PUBLIC_BASE_URL;
    }

    public static string GetSignedOrPublicUrl(string rawPath, int expirySeconds = DefaultExpirySeconds)
    {
        if (string.IsNullOrWhiteSpace(rawPath))
        {
            return rawPath;
        }

        EnsureConfigLoaded();
        var objectKey = ExtractObjectKey(rawPath, cachedConfig?.R2_BUCKET);
        if (string.IsNullOrEmpty(objectKey))
        {
            return rawPath;
        }

        if (HasSigningMaterial(cachedConfig))
        {
            try
            {
                return BuildPresignedUrl(objectKey, expirySeconds);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"CloudflareR2UrlBuilder: Failed to create signed URL, using public URL. {ex.Message}");
            }
        }

        return BuildPublicUrl(objectKey, rawPath);
    }

    private static bool HasSigningMaterial(R2EnvConfig config)
    {
        return config != null
            && !string.IsNullOrWhiteSpace(config.R2_ACCESS_KEY)
            && !string.IsNullOrWhiteSpace(config.R2_SECRET_KEY)
            && !string.IsNullOrWhiteSpace(config.R2_BUCKET)
            && !string.IsNullOrWhiteSpace(config.R2_ENDPOINT);
    }

    private static void EnsureConfigLoaded()
    {
        if (attemptedLoad)
        {
            return;
        }

        attemptedLoad = true;
        var envPath = Path.Combine(Application.dataPath, "env.json");
        if (!File.Exists(envPath))
        {
            Debug.LogWarning($"CloudflareR2UrlBuilder: env.json not found at {envPath}");
            return;
        }

        try
        {
            var json = File.ReadAllText(envPath);
            cachedConfig = JsonUtility.FromJson<R2EnvConfig>(json);
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"CloudflareR2UrlBuilder: Failed to read env.json: {ex.Message}");
        }
    }

    private static string BuildPublicUrl(string objectKey, string fallback)
    {
        if (cachedConfig == null || string.IsNullOrWhiteSpace(cachedConfig.R2_PUBLIC_BASE_URL))
        {
            return fallback;
        }

        var baseUrl = cachedConfig.R2_PUBLIC_BASE_URL.TrimEnd('/');
        return $"{baseUrl}/{EscapePath(objectKey)}";
    }

    private static string BuildPresignedUrl(string objectKey, int expirySeconds)
    {
        if (cachedConfig == null)
        {
            throw new InvalidOperationException("CloudflareR2UrlBuilder: Configuration not loaded.");
        }

        var endpoint = cachedConfig.R2_ENDPOINT?.TrimEnd('/');
        var bucket = cachedConfig.R2_BUCKET;
        var accessKey = cachedConfig.R2_ACCESS_KEY;
        var secretKey = cachedConfig.R2_SECRET_KEY;

        if (string.IsNullOrWhiteSpace(endpoint) || string.IsNullOrWhiteSpace(bucket))
        {
            throw new InvalidOperationException("CloudflareR2UrlBuilder: Missing endpoint or bucket.");
        }

        var host = new Uri(endpoint).Host;
        var canonicalUri = $"/{bucket}/{EscapePath(objectKey)}";

        var now = DateTime.UtcNow;
        var amzDate = now.ToString("yyyyMMdd'T'HHmmss'Z'");
        var dateStamp = now.ToString("yyyyMMdd");
        var credentialScope = $"{dateStamp}/{Region}/s3/aws4_request";

        var queryParams = new SortedDictionary<string, string>
        {
            ["X-Amz-Algorithm"] = "AWS4-HMAC-SHA256",
            ["X-Amz-Credential"] = $"{accessKey}/{credentialScope}",
            ["X-Amz-Date"] = amzDate,
            ["X-Amz-Expires"] = Mathf.Clamp(expirySeconds, 60, 604800).ToString(),
            ["X-Amz-SignedHeaders"] = "host"
        };

        var canonicalQuery = BuildCanonicalQueryString(queryParams);
        var canonicalHeaders = $"host:{host}\n";
        const string signedHeaders = "host";
        const string payloadHash = "UNSIGNED-PAYLOAD";

        var canonicalRequest = $"GET\n{canonicalUri}\n{canonicalQuery}\n{canonicalHeaders}\n{signedHeaders}\n{payloadHash}";
        var hashedRequest = HashHex(canonicalRequest);
        var stringToSign = $"AWS4-HMAC-SHA256\n{amzDate}\n{credentialScope}\n{hashedRequest}";

        var signingKey = GetSignatureKey(secretKey, dateStamp, Region, "s3");
        var signature = HmacHex(signingKey, stringToSign);

        var finalQuery = $"{canonicalQuery}&X-Amz-Signature={signature}";
        return $"{endpoint}{canonicalUri}?{finalQuery}";
    }

    private static string ExtractObjectKey(string rawPath, string bucket)
    {
        if (string.IsNullOrWhiteSpace(rawPath))
        {
            return string.Empty;
        }

        if (Uri.TryCreate(rawPath, UriKind.Absolute, out var uri))
        {
            var path = uri.AbsolutePath.TrimStart('/');
            if (!string.IsNullOrWhiteSpace(bucket) && path.StartsWith(bucket + "/", StringComparison.OrdinalIgnoreCase))
            {
                path = path.Substring(bucket.Length + 1);
            }
            return path;
        }

        return rawPath.TrimStart('/');
    }

    private static string EscapePath(string path)
    {
        var segments = path.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < segments.Length; i++)
        {
            segments[i] = Uri.EscapeDataString(segments[i]);
        }
        return string.Join("/", segments);
    }

    private static string BuildCanonicalQueryString(SortedDictionary<string, string> queryParams)
    {
        var builder = new StringBuilder();
        foreach (var kvp in queryParams)
        {
            if (builder.Length > 0)
            {
                builder.Append('&');
            }
            builder.Append(Uri.EscapeDataString(kvp.Key));
            builder.Append('=');
            builder.Append(Uri.EscapeDataString(kvp.Value));
        }
        return builder.ToString();
    }

    private static string HashHex(string value)
    {
        using (var sha = SHA256.Create())
        {
            var bytes = Encoding.UTF8.GetBytes(value);
            var hash = sha.ComputeHash(bytes);
            return BytesToHex(hash);
        }
    }

    private static byte[] GetSignatureKey(string key, string dateStamp, string regionName, string serviceName)
    {
        var kDate = Hmac(Encoding.UTF8.GetBytes("AWS4" + key), dateStamp);
        var kRegion = Hmac(kDate, regionName);
        var kService = Hmac(kRegion, serviceName);
        return Hmac(kService, "aws4_request");
    }

    private static string HmacHex(byte[] key, string data)
    {
        return BytesToHex(Hmac(key, data));
    }

    private static byte[] Hmac(byte[] key, string data)
    {
        using (var hmac = new HMACSHA256(key))
        {
            return hmac.ComputeHash(Encoding.UTF8.GetBytes(data));
        }
    }

    private static string BytesToHex(byte[] bytes)
    {
        var builder = new StringBuilder(bytes.Length * 2);
        for (int i = 0; i < bytes.Length; i++)
        {
            builder.Append(bytes[i].ToString("x2"));
        }
        return builder.ToString();
    }
}
