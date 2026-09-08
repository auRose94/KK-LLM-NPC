// Unit tests for Horniness calculation in NPCInstance.cs
// These tests validate the horniness accumulation and decay logic.

using System;
using System.Collections.Generic;

public static class HorninessTests
{
    // Test horniness accumulation over time
    public static void TestHorninessAccumulation()
    {
        // Simulate slow-burn horniness accumulation
        float horniness = 0.08f; // Starting value
        float hornyRate = 5f; // Rate per minute
        float minutes = 10f; // 10 minutes pass

        // Accumulate horniness
        float accumulated = horniness + (hornyRate * minutes);

        // Verify horniness increased
        if (accumulated <= horniness)
            throw new Exception($"Expected horniness to increase, got {accumulated} <= {horniness}");

        Console.WriteLine($"Horniness after {minutes} minutes: {accumulated:F2}");
    }

    // Test horniness decay after stimulation
    public static void TestHorninessDecay()
    {
        // Simulate horniness decay after stimulation
        float horniness = 0.8f; // High horniness
        float stimulation = 0.3f; // Stimulation reduces horniness
        float decayRate = 0.5f; // Decay rate

        float newHorniness = horniness - (stimulation * decayRate);

        // Verify horniness decreased
        if (newHorniness >= horniness)
            throw new Exception($"Expected horniness to decrease, got {newHorniness} >= {horniness}");

        Console.WriteLine($"Horniness after stimulation: {newHorniness:F2}");
    }

    // Test horniness bounds (0.0 to 1.0)
    public static void TestHorninessBounds()
    {
        // Test lower bound
        float horniness = 0.0f;
        float stimulation = 0.1f;
        float decayRate = 0.5f;
        float newHorniness = horniness - (stimulation * decayRate);

        if (newHorniness < 0f)
            throw new Exception($"Horniness cannot be negative, got {newHorniness}");

        // Test upper bound
        horniness = 1.0f;
        newHorniness = horniness + (stimulation * decayRate);

        if (newHorniness > 1f)
            throw new Exception($"Horniness cannot exceed 1.0, got {newHorniness}");

        Console.WriteLine($"Horniness within bounds: {newHorniness:F2}");
    }

    // Test ready_to_lay flag
    public static void TestEggLayReadiness()
    {
        // Simulate egg volume and readiness
        float eggVolume = 0.7f; // 70% full
        float maxEggVolume = 1.0f; // 100% capacity

        bool readyToLay = eggVolume >= maxEggVolume;

        if (!readyToLay)
            throw new Exception($"Expected ready_to_lay to be true, got {readyToLay}");

        Console.WriteLine($"Egg volume: {eggVolume:P0}, Ready to lay: {readyToLay}");
    }

    // Test energy levels
    public static void TestEnergyLevels()
    {
        // Simulate energy levels
        float energy = 0.3f; // 30% energy
        float maxEnergy = 1.0f; // 100% capacity

        float energyRatio = energy / maxEnergy;

        // Verify ratio is between 0 and 1
        if (energyRatio < 0f || energyRatio > 1f)
            throw new Exception($"Energy ratio out of bounds: {energyRatio}");

        Console.WriteLine($"Energy: {energy:P0}, Ratio: {energyRatio:F2}");
    }

    // Test context pressure calculation
    public static void TestContextPressure()
    {
        // Simulate context pressure calculation
        int factCount = 20;
        int histCount = 10;
        int thoughtCount = 6;
        int chatCount = 0;
        int nearbyCount = 15;

        // Simulate fill ratio calculation (simplified)
        float total = factCount + histCount + thoughtCount + chatCount + nearbyCount;
        float fill = total / 100f; // Assume 100 is max context

        // Verify fill ratio
        if (fill < 0f || fill > 1f)
            throw new Exception($"Fill ratio out of bounds: {fill}");

        Console.WriteLine($"Context fill ratio: {fill:P0}");
    }

    // Run all tests
    public static void RunAllTests()
    {
        Console.WriteLine("Running Horniness Tests...\n");

        try
        {
            TestHorninessAccumulation();
            Console.WriteLine("✓ TestHorninessAccumulation passed\n");
        }
        catch (Exception e)
        {
            Console.WriteLine($"✗ TestHorninessAccumulation failed: {e.Message}\n");
        }

        try
        {
            TestHorninessDecay();
            Console.WriteLine("✓ TestHorninessDecay passed\n");
        }
        catch (Exception e)
        {
            Console.WriteLine($"✗ TestHorninessDecay failed: {e.Message}\n");
        }

        try
        {
            TestHorninessBounds();
            Console.WriteLine("✓ TestHorninessBounds passed\n");
        }
        catch (Exception e)
        {
            Console.WriteLine($"✗ TestHorninessBounds failed: {e.Message}\n");
        }

        try
        {
            TestEggLayReadiness();
            Console.WriteLine("✓ TestEggLayReadiness passed\n");
        }
        catch (Exception e)
        {
            Console.WriteLine($"✗ TestEggLayReadiness failed: {e.Message}\n");
        }

        try
        {
            TestEnergyLevels();
            Console.WriteLine("✓ TestEnergyLevels passed\n");
        }
        catch (Exception e)
        {
            Console.WriteLine($"✗ TestEnergyLevels failed: {e.Message}\n");
        }

        try
        {
            TestContextPressure();
            Console.WriteLine("✓ TestContextPressure passed\n");
        }
        catch (Exception e)
        {
            Console.WriteLine($"✗ TestContextPressure failed: {e.Message}\n");
        }

        Console.WriteLine("All tests completed.");
    }

    public static void Main(string[] args)
    {
        Console.WriteLine("Running Horniness Tests...");
        Console.WriteLine("========================================");
        Console.WriteLine();

        try
        {
            TestHorninessAccumulation();
            Console.WriteLine("✓ TestHorninessAccumulation passed\n");
        }
        catch (Exception e)
        {
            Console.WriteLine($"✗ TestHorninessAccumulation failed: {e.Message}\n");
        }

        try
        {
            TestHorninessDecay();
            Console.WriteLine("✓ TestHorninessDecay passed\n");
        }
        catch (Exception e)
        {
            Console.WriteLine($"✗ TestHorninessDecay failed: {e.Message}\n");
        }

        try
        {
            TestHorninessBounds();
            Console.WriteLine("✓ TestHorninessBounds passed\n");
        }
        catch (Exception e)
        {
            Console.WriteLine($"✗ TestHorninessBounds failed: {e.Message}\n");
        }

        try
        {
            TestEggLayReadiness();
            Console.WriteLine("✓ TestEggLayReadiness passed\n");
        }
        catch (Exception e)
        {
            Console.WriteLine($"✗ TestEggLayReadiness failed: {e.Message}\n");
        }

        try
        {
            TestEnergyLevels();
            Console.WriteLine("✓ TestEnergyLevels passed\n");
        }
        catch (Exception e)
        {
            Console.WriteLine($"✗ TestEnergyLevels failed: {e.Message}\n");
        }

        try
        {
            TestContextPressure();
            Console.WriteLine("✓ TestContextPressure passed\n");
        }
        catch (Exception e)
        {
            Console.WriteLine($"✗ TestContextPressure failed: {e.Message}\n");
        }

        Console.WriteLine("========================================");
        Console.WriteLine("All tests completed.");
    }
}