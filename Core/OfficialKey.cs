namespace AutoReplay.Core
{
    /// <summary>
    /// 官方模型的签名公钥 (RSA-2048). 私钥只在作者本机, 不进 repo.
    /// 用 tools/ApModelTool 的 keygen 产生; 换钥匙要同时更新这里.
    /// </summary>
    public static class OfficialKey
    {
        public const string PublicKeyXml =
            "<RSAKeyValue><Modulus>v4zA84d7tFRps7/WDIPXsMMypsYhaD/Jf8QLVL7HQxiqmm+aaFUS3sl0b0mWuKm31JjaGU9C/jJW13fJmQX/9z0wbmatFgUhcQMM5+lS4DrvJDgPt5luKXw1xFAYfCmSOyPTTXJJFbGTDF+imHMvnZBmdg7OqlStEy9HmTfQk0ORJvKU7u3RBZVF8o2X5E7y/rRRQ7PK151f5N4Qg6Smjl/LITmScd+//sG4lymu8fQEIz9dfqHx952oj8bVwrKcA9GZNqyKh3D/28EWKRBT+QinsdIhT9LHlCKjpJkrr+dFdB0w8AIZLfw1dJvmu6ODD3sxbf6CERTTTsx1vx6ncQ==</Modulus><Exponent>AQAB</Exponent></RSAKeyValue>";
    }
}
