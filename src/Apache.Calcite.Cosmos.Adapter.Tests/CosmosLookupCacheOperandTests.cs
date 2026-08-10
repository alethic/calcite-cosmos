using System;

using FluentAssertions;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Apache.Calcite.Cosmos.Adapter.Tests
{

    /// <summary>
    /// Covers the lookup cache operands' parsing — the only part of the policy that arrives as
    /// untyped model JSON.
    /// </summary>
    [TestClass]
    public class CosmosLookupCacheOperandTests
    {

        static java.util.HashMap Operand(object? maxRows, object? expireSeconds)
        {
            var operand = new java.util.HashMap();
            if (maxRows is not null)
                operand.put(CosmosSchemaFactory.LookupCacheMaxRowsOperand, maxRows);
            if (expireSeconds is not null)
                operand.put(CosmosSchemaFactory.LookupCacheExpireSecondsOperand, expireSeconds);
            return operand;
        }

        [TestMethod]
        public void BothOperandsReadAsAPolicy()
        {
            var policy = CosmosSchemaFactory.ReadLookupCache(Operand("10000", "300"));

            policy.Should().NotBeNull();
            policy!.Value.MaxRows.Should().Be(10000);
            policy.Value.ExpireAfterWrite.Should().Be(TimeSpan.FromSeconds(300));
        }

        [TestMethod]
        public void NeitherOperandReadsAsNoCache()
        {
            CosmosSchemaFactory.ReadLookupCache(Operand(null, null)).Should().BeNull();
        }

        /// <remarks>
        /// A cache without a bound or without a freshness policy is not something to guess into
        /// existence, so half a configuration is a model mistake and says so.
        /// </remarks>
        [TestMethod]
        public void HalfAConfigurationIsAModelError()
        {
            var rowsOnly = () => CosmosSchemaFactory.ReadLookupCache(Operand("10000", null));
            rowsOnly.Should().Throw<ArgumentException>().WithMessage("*come together*");

            var expireOnly = () => CosmosSchemaFactory.ReadLookupCache(Operand(null, "300"));
            expireOnly.Should().Throw<ArgumentException>().WithMessage("*come together*");
        }

        [TestMethod]
        public void ANonPositiveValueIsAModelError()
        {
            var zero = () => CosmosSchemaFactory.ReadLookupCache(Operand("0", "300"));
            zero.Should().Throw<ArgumentException>().WithMessage("*positive*");

            var word = () => CosmosSchemaFactory.ReadLookupCache(Operand("10000", "soon"));
            word.Should().Throw<ArgumentException>().WithMessage("*positive*");
        }

    }

}
