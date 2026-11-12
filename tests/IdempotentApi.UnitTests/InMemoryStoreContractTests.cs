using IdempotentApi.ContractTests;

namespace IdempotentApi.UnitTests;

public sealed class InMemoryStoreContractTests : IdempotencyStoreContract
{
    protected override IIdempotencyStore Store { get; } = new InMemoryIdempotencyStore();
}
