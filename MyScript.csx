// MyScript.csx – Example script
using System;

Console.WriteLine("✅ MyScript.csx loaded successfully!");

public class Calculator
{
    public int Add(int a, int b) => a + b;
    public int Multiply(int a, int b) => a * b;
}

var calc = new Calculator();
Console.WriteLine($"5 + 7 = {calc.Add(5, 7)}");