# ZMapper v1.0.0 — Pitfalls & Lessons Learned

This document describes issues encountered when integrating the ZMapper source-generated mapper library into the Fakvio project.

## 1. Namespace Split

`IMapperProfile` interface is in `ZMapper.Abstractions.Configuration`, but the `MapperConfiguration` class is in the `ZMapper` namespace. Both `using` statements are required:

```csharp
using ZMapper;
using ZMapper.Abstractions.Configuration;
```

**Fix:** Add both usings in every profile class, or use a global using in the `.csproj`.

## 2. Generated Code Namespace

The source generator emits extension methods and the `Mapper` class into the **same namespace as the profile classes** (e.g., `Fakvio.Infrastructure.Mapping`), NOT into the `ZMapper` namespace. This means:

- Services calling `.ToXxxDto()` need `using Fakvio.Infrastructure.Mapping;`
- The `AddZMapper()` DI extension is also in the Mapping namespace

**Fix:** Add `<Using Include="Fakvio.Infrastructure.Mapping" />` as a global using in the `.csproj`.

## 3. Generated Code Missing `using ZMapper`

The generated `Mapper` class references `MapperConfiguration` but the generated file does not include `using ZMapper;`. This causes `CS0246: MapperConfiguration not found` in the generated `ZMapper_Mapper.g.cs`.

**Fix:** Add `<Using Include="ZMapper" />` as a global using in the Infrastructure `.csproj` so the generated code can resolve it.

## 4. `ForMember` Configurations Are Ignored in Generated Extension Methods

The source generator creates direct property-to-property assignments in the `.ToXxxDto()` extension methods. **`ForMember` transformations are NOT applied.** They are effectively dead code — the generator ignores them entirely.

For example, this profile config:
```csharp
config.CreateMap<Invoice, InvoiceDto>()
    .ForMember(dest => dest.IssueDate, opt => opt.MapFrom(src => src.IssueDate ?? DateTime.UtcNow))
    .ForMember(dest => dest.ClientName, opt => opt.MapFrom(src => src.Client.CompanyName));
```

Generates this (ignoring ForMember):
```csharp
destination.IssueDate = source.IssueDate;  // direct assignment, no null-coalescing
// ClientName is not mapped at all (navigation property)
```

**Impact:** Navigation property projections and null-coalescing conversions must be done manually in the service layer after calling the extension method.

## 5. `IgnoreNonExisting()` Hides Inherited Properties

When using `IgnoreNonExisting()`, the source generator also skips properties inherited from base classes (like `BaseEntity`). This means `Id`, `CreatedAt`, and `UpdatedAt` are NOT mapped, even though they exist on both the source entity and destination DTO.

**Impact:** Every DTO that needs `Id` must have it assigned manually:
```csharp
var dto = entity.ToVatRateDto();
dto.Id = entity.Id;
dto.CreatedAt = entity.CreatedAt;
dto.UpdatedAt = entity.UpdatedAt;
```

## 6. Nullable Value Type Mismatches

The source generator performs direct assignments without null handling. If the source property is `DateTime?` but the destination is `DateTime`, the generated code produces `CS0266: Cannot implicitly convert type 'DateTime?' to 'DateTime'`.

Reference type mismatches (`string?` → `string`) are suppressed by a `#pragma warning disable CS8601` in the generated file, but value type mismatches cannot be suppressed.

**Fix:** Make DTO properties nullable to match the source entity, or handle the conversion manually.

## 7. Profile Classes Must Be `partial`

The source generator creates a partial counterpart for each profile class. If the `partial` keyword is missing, you get `CS0260: Missing partial modifier on declaration`.

```csharp
public partial class VatRateProfile : IMapperProfile  // ✅ partial required
```

## 8. `EmitCompilerGeneratedFiles` for Debugging

To inspect what the source generator produces, add to `.csproj`:

```xml
<EmitCompilerGeneratedFiles>true</EmitCompilerGeneratedFiles>
<CompilerGeneratedFilesOutputPath>Generated</CompilerGeneratedFilesOutputPath>
```

And exclude from compilation:
```xml
<ItemGroup>
    <Compile Remove="$(CompilerGeneratedFilesOutputPath)/**" />
</ItemGroup>
```

## Summary of Workarounds

| Issue | Workaround |
|-------|-----------|
| Namespace split | Both `using ZMapper;` and `using ZMapper.Abstractions.Configuration;` |
| Extension methods not found | Global using for `Fakvio.Infrastructure.Mapping` |
| ForMember ignored | Set navigation properties manually in service |
| BaseEntity props not mapped | Set `Id`, `CreatedAt`, `UpdatedAt` manually |
| Nullable type mismatch | Make DTO props nullable or handle manually |
| Generated code missing using | Global `<Using Include="ZMapper" />` in csproj |
