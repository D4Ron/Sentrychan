using Avalonia.Platform;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Sentrychan.UI.Services;

public class QuoteService
{
    private readonly List<string> _quotes = new();

    public QuoteService()
    {
        try
        {
            var assets = AssetLoader.Open(new Uri("avares://Sentrychan.UI/Assets/quotes.txt"));
            using var reader = new StreamReader(assets);
            while (reader.ReadLine() is string line)
            {
                if (!string.IsNullOrWhiteSpace(line))
                {
                    _quotes.Add(line.Trim());
                }
            }
        }
        catch (Exception ex)
        {
            // Log warning or just fail gracefully
            System.Diagnostics.Debug.WriteLine($"Failed to load quotes: {ex.Message}");
        }

        if (_quotes.Count == 0)
        {
            _quotes.Add("Welcome back!");
        }
    }

    public string GetRandom()
    {
        return _quotes[Random.Shared.Next(_quotes.Count)];
    }
}
