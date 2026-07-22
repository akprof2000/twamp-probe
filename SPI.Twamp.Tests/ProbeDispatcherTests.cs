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

        [Theory(DisplayName = "0 — автоподбор ядра × 16 с потолком 10000 и полом 16")]
        [InlineData(0, 8, 128)]        // 8 × 16 = 128
        [InlineData(0, 16, 256)]       // 16 × 16 = 256
        [InlineData(0, 128, 2048)]     // 128 × 16 = 2048 (в пределах потолка)
        [InlineData(0, 625, 10000)]    // 625 × 16 = 10000 (на потолке)
        [InlineData(0, 1000, 10000)]   // 1000 × 16 = 16000 → потолок 10000
        [InlineData(0, 1, 16)]         // 1 × 16 = 16 (пол)
        [InlineData(-5, 4, 64)]        // отрицательное трактуется как авто
        public void Auto_ByFormulaWithBounds(int configured, int cores, int expected) =>
            Assert.Equal(expected, ProbeDispatcher.ResolveWorkerCount(configured, cores));
    }
}
