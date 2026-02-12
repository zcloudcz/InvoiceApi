namespace InvoiceApi.Application.QrPayment;

/// <summary>
/// CRC32 calculator for the QR Faktura (SIND) standard.
///
/// The CRC32 checksum is computed from the canonical SIND string:
/// 1. Take all attributes EXCEPT CRC32 itself
/// 2. Sort attributes alphabetically by key
/// 3. Build string: SID*1.0*{sorted key:value pairs each ending with *}
/// 4. Compute CRC32 of the resulting string
/// 5. Output as 8-character uppercase hex string (e.g., "1234ABCD")
///
/// Uses the standard CRC32 polynomial (0xEDB88320) — same as used in ZIP, PNG, etc.
/// </summary>
public static class Crc32Calculator
{
    // Pre-computed CRC32 lookup table for the standard polynomial 0xEDB88320.
    // Building the table once avoids recomputing bit shifts for every byte.
    private static readonly uint[] Table = BuildTable();

    /// <summary>
    /// Builds the 256-entry CRC32 lookup table using the standard polynomial.
    /// Each entry represents the CRC contribution for a single byte value (0-255).
    /// </summary>
    private static uint[] BuildTable()
    {
        const uint polynomial = 0xEDB88320;
        var table = new uint[256];

        for (uint i = 0; i < 256; i++)
        {
            var crc = i;
            for (int j = 0; j < 8; j++)
            {
                // If the least significant bit is 1, XOR with the polynomial
                crc = (crc & 1) != 0
                    ? (crc >> 1) ^ polynomial
                    : crc >> 1;
            }
            table[i] = crc;
        }

        return table;
    }

    /// <summary>
    /// Computes the CRC32 checksum of a UTF-8 encoded string.
    /// Returns the result as an 8-character uppercase hexadecimal string.
    /// </summary>
    /// <param name="input">The string to compute CRC32 for</param>
    /// <returns>8-character hex string, e.g., "1234ABCD"</returns>
    public static string Compute(string input)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(input);
        // CRC32 starts with all bits set (0xFFFFFFFF)
        uint crc = 0xFFFFFFFF;

        foreach (var b in bytes)
        {
            // XOR the current byte with the low 8 bits of the CRC,
            // then look up the table entry and XOR with the shifted CRC
            crc = (crc >> 8) ^ Table[(crc ^ b) & 0xFF];
        }

        // Final XOR with 0xFFFFFFFF (invert all bits) — standard CRC32 finalization
        crc ^= 0xFFFFFFFF;

        // Format as 8-character uppercase hex (padded with leading zeros if needed)
        return crc.ToString("X8");
    }
}
