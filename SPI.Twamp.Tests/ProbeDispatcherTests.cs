// Ignore Spelling: SPI Twamp

using SPI.Twamp.Probe.Server;
using Xunit;

namespace SPI.Twamp.Tests
{
    /// <summary>
    /// Тесты автоподбора числа воркеров пробы (Probe:MaxParallel).
    /// </summary>
    public class ProbeDispatcherTests
    {
        [Theory(DisplayName = "Явное значение (> 0) используется как есть")]
        [InlineData(1024, 8, 1024)]
        [InlineData(50, 64, 50)]
        [InlineData(1, 16, 1)]
        public void Explicit_UsedAsIs(int configured, int cores, int expected) =>
            Assert.Equal(expected, ProbeDispatcher.ResolveWorkerCount(configured, cores));

        [Theory(DisplayName = "0 — автоподбор ядра × 10 с потолком 10000 и полом 16")]
        [InlineData(0, 8, 80)]         // 8 × 10 = 80
        [InlineData(0, 16, 160)]       // 16 × 10 = 160
        [InlineData(0, 128, 1280)]     // 128 × 10 = 1280 (в пределах потолка)
        [InlineData(0, 1000, 10000)]   // 1000 × 10 = 10000 (на потолке)
        [InlineData(0, 2000, 10000)]   // 2000 × 10 = 20000 → потолок 10000
        [InlineData(0, 1, 16)]         // 1 × 10 = 10 → пол 16
        [InlineData(-5, 4, 40)]        // отрицательное трактуется как авто
        public void Auto_ByFormulaWithBounds(int configured, int cores, int expected) =>
            Assert.Equal(expected, ProbeDispatcher.ResolveWorkerCount(configured, cores));
    }
}
