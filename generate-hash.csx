#!/usr/bin/env dotnet-script
#r "nuget: BCrypt.Net-Next, 4.0.3"

using BCrypt.Net;

var password = "Invoice123";
var hash = BCrypt.HashPassword(password, workFactor: 12);
Console.WriteLine($"Password: {password}");
Console.WriteLine($"BCrypt Hash: {hash}");
