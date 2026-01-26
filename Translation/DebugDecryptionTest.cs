using Microsoft.AspNetCore.DataProtection;
using NUnit.Framework;

namespace IntegrationTest.Translation;

[TestFixture]
[Category("Debug")]
public class DebugDecryptionTest
{
    [Test]
    public void Debug_ShowDecryptionAttempt()
    {
        var encryptedValue = "ENC:CfDJ8FJC5Stg7nBBiecYHyWndlwCda7yuIJjCWiHe358twZKLglyhjl90FLNHuyfPrSFfEYJpGUYucwxaDHuTgAnPYU2OCRoTNNni4aHNf7ngS9_Vmov__NZG8rPipY7JWrepcOqf9avEFAqH8X4nOxx1jxZfQ4LrjRnDVoxf6xG357P";

        var possiblePaths = new[]
        {
            "/mnt/c/Users/hgasp/AppData/Local/Klacks/DataProtection-Keys",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Klacks", "DataProtection-Keys")
        };

        foreach (var path in possiblePaths)
        {
            TestContext.WriteLine($"Testing path: {path}");
            TestContext.WriteLine($"  Exists: {Directory.Exists(path)}");

            if (!Directory.Exists(path))
            {
                continue;
            }

            var files = Directory.GetFiles(path, "*.xml");
            TestContext.WriteLine($"  Key files: {files.Length}");

            try
            {
                var provider = DataProtectionProvider.Create(
                    new DirectoryInfo(path),
                    configuration => configuration.SetApplicationName("Klacks"));
                var protector = provider.CreateProtector("Klacks.Settings.Encryption");

                var cipherText = encryptedValue.Substring(4);
                var decrypted = protector.Unprotect(cipherText);

                TestContext.WriteLine($"  SUCCESS! Decrypted length: {decrypted.Length}");
                TestContext.WriteLine($"  First 20 chars: {decrypted.Substring(0, Math.Min(20, decrypted.Length))}...");
                return;
            }
            catch (Exception ex)
            {
                TestContext.WriteLine($"  FAILED: {ex.GetType().Name}: {ex.Message}");
            }
        }

        Assert.Fail("Could not decrypt with any path");
    }
}
