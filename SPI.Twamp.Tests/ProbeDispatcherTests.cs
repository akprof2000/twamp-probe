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

        [Theory(DisplayName = "0 — автоподбор ядра × 64 с потолком 10000 и полом 16")]
        [InlineData(0, 8, 512)]        // 8 × 64 = 512
        [InlineData(0, 16, 1024)]      // 16 × 64 = 1024
        [InlineData(0, 128, 8192)]     // 128 × 64 = 8192 (в пределах потолка)
        [InlineData(0, 156, 9984)]     // 156 × 64 = 9984 (у самого потолка)
        [InlineData(0, 200, 10000)]    // 200 × 64 = 12800 → потолок 10000
        [InlineData(0, 1, 64)]         // 1 × 64 = 64
        [InlineData(-5, 4, 256)]       // отрицательное трактуется как авто
        public void Auto_ByFormulaWithBounds(int configured, int cores, int expected) =>
            Assert.Equal(expected, ProbeDispatcher.ResolveWorkerCount(configured, cores));
    }
}
