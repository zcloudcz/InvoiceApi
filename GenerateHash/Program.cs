var password = "Invoice123";
var hash = BCrypt.Net.BCrypt.HashPassword(password, workFactor: 12);
Console.WriteLine($"BCrypt Hash for '{password}': {hash}");
