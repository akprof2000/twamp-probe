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

        [Theory(DisplayName = "0 — автоподбор ядра × 256 с потолком 100000 и полом 16")]
        [InlineData(0, 8, 2048)]       // 8 × 256 = 2048
        [InlineData(0, 16, 4096)]      // 16 × 256 = 4096
        [InlineData(0, 128, 32768)]    // 128 × 256 = 32768 (в пределах потолка)
        [InlineData(0, 390, 99840)]    // 390 × 256 = 99840 (у самого потолка)
        [InlineData(0, 500, 100000)]   // 500 × 256 = 128000 → потолок 100000
        [InlineData(0, 1, 256)]        // 1 × 256 = 256
        [InlineData(-5, 4, 1024)]      // отрицательное трактуется как авто
        public void Auto_ByFormulaWithBounds(int configured, int cores, int expected) =>
            Assert.Equal(expected, ProbeDispatcher.ResolveWorkerCount(configured, cores));
    }
}
