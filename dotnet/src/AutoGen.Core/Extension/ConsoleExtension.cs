// Copyright (c) Microsoft Corporation. All rights reserved.
// ConsoleExtensions.cs

using System;
using System.IO;

namespace AutoGen.Core;

public static class ConsoleExtension
{
    public static void WriteColouredLine(
        this TextWriter writer,
        ConsoleColor colour,
        string message
    )
    {
        var originalColor = Console.ForegroundColor;
        try
        {
            Console.ForegroundColor = colour;
            writer.WriteLine(message);
        }
        finally
        {
            Console.ForegroundColor = originalColor;
        }
        //Console.ForegroundColor = colour;
        //Console.WriteLine(message);
        //Console.ResetColor();
    }
}
