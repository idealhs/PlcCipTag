# PlcCipTag

CIP protocol Tag-based PLC communication library for .NET, supporting Allen-Bradley, ICON, and Omron PLCs via EtherNet/IP.

## Supported PLCs

| Class | PLC | Backend |
| --- | --- | --- |
| `PLC_OpenAb` | Allen-Bradley ControlLogix | [libplctag.NET](https://github.com/libplctag/libplctag.NET) |
| `PLC_Icon` | ICON (Omron-based) | libplctag.NET |
| `PLC_OMRON` | Omron NJ/NX | Native CIP over TCP |

All implementations share the `ITagPlc` interface.

## Data Types

| Type | Read | Write | Array Read | Array Write |
| --- | --- | --- | --- | --- |
| float (REAL) | `ReadFloat` | `WriteFloat` | `ReadFloatArray` | `WriteFloatArray` |
| int (DINT) | — | — | `ReadDINTArray` | `WriteDINTArray` |
| bool (BOOL) | — | `WriteBool` | `ReadBoolArray` | `WriteBoolArray` |
| string (STRING) | — | `WriteString` | `ReadStringArray` | `WriteStringArray` |

Every method has a corresponding `Async` variant.

## Usage

```csharp
// Allen-Bradley
using ITagPlc plc = new PLC_OpenAb("192.168.1.10");

float value = plc.ReadFloat("MyTag");
plc.WriteFloatArray("MyArray", new float[] { 1.0f, 2.0f, 3.0f });

// Omron
using ITagPlc omron = new PLC_OMRON("192.168.1.20", path: "1,0");

var data = await omron.ReadFloatArrayAsync("SensorData[0]", 100);
```

## Installation

```xml
<PackageReference Include="PlcCipTag" Version="*" />
```

Or build from source:

```bash
dotnet build PlcCipTag.csproj
```

## Target Frameworks

`netstandard2.0` · `net6.0` · `net8.0` · `net9.0` · `net10.0`

## License

Apache-2.0
