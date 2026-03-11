# Random Access Binary File I/O in C#

## Overview

When working with large binary files, reading the entire file into memory is often unnecessary and inefficient. If your data is structured so that each record corresponds to a known position in the file, you can use **random access I/O** to seek directly to any record in O(1) time — regardless of file size.

This report covers two implementation strategies:

1. **Fixed-size records** — the simplest and most performant approach
2. **Variable-size records with an index file** — for when record sizes differ

---

## Strategy 1: Fixed-Size Records

### How It Works

If every record is exactly the same number of bytes, the byte offset of any record can be calculated directly:

```
byte offset = recordIndex * recordSizeInBytes
```

You then use `FileStream.Seek()` to jump to that offset and read only the bytes you need.

### Writing the File

```csharp
const int RecordSize = 64; // bytes per record
const string FilePath = "data.bin";

using (var fs = new FileStream(FilePath, FileMode.Create, FileAccess.Write))
using (var writer = new BinaryWriter(fs))
{
    for (int i = 0; i < 100_000; i++)
    {
        byte[] record = new byte[RecordSize];

        // Write the index as the first 4 bytes
        BitConverter.GetBytes(i).CopyTo(record, 0);

        // Write a sample value as the next 8 bytes
        BitConverter.GetBytes((double)i * 1.5).CopyTo(record, 4);

        // Remaining bytes stay as zero padding
        writer.Write(record);
    }
}
```

### Reading a Specific Record

```csharp
int targetIndex = 75_000;

using (var fs = new FileStream(FilePath, FileMode.Open, FileAccess.Read))
using (var reader = new BinaryReader(fs))
{
    long offset = (long)targetIndex * RecordSize;
    fs.Seek(offset, SeekOrigin.Begin); // Jump directly — no scanning

    byte[] record = reader.ReadBytes(RecordSize);

    int storedIndex  = BitConverter.ToInt32(record, 0);
    double storedVal = BitConverter.ToDouble(record, 4);

    Console.WriteLine($"Index: {storedIndex}, Value: {storedVal}");
}
```

### SeekOrigin Options

| Option              | Description                          |
|---------------------|--------------------------------------|
| `SeekOrigin.Begin`  | Offset from the start of the file    |
| `SeekOrigin.Current`| Offset from the current position     |
| `SeekOrigin.End`    | Offset backwards from the end        |

For index-based lookups, always use `SeekOrigin.Begin`.

---

## Strategy 2: Variable-Size Records with an Index File

When records differ in size, you cannot calculate offsets mathematically. Instead, maintain a **separate index file** that stores the byte offset of each record in the data file.

### File Structure

```
index.bin  →  [ offset_0 ][ offset_1 ][ offset_2 ] ... [ offset_N ]
                 8 bytes     8 bytes     8 bytes           8 bytes

data.bin   →  [ record_0 (variable) ][ record_1 (variable) ] ...
```

### Writing Both Files

```csharp
const string DataPath  = "data.bin";
const string IndexPath = "index.bin";

string[] records = Enumerable.Range(0, 100_000)
    .Select(i => $"Record number {i}: value={i * 1.5:F2}")
    .ToArray();

using (var dataStream  = new FileStream(DataPath,  FileMode.Create, FileAccess.Write))
using (var indexStream = new FileStream(IndexPath, FileMode.Create, FileAccess.Write))
using (var dataWriter  = new BinaryWriter(dataStream))
using (var indexWriter = new BinaryWriter(indexStream))
{
    foreach (string record in records)
    {
        // Write the current data offset to the index
        indexWriter.Write(dataStream.Position);

        // Write the record: length-prefixed string
        byte[] bytes = Encoding.UTF8.GetBytes(record);
        dataWriter.Write(bytes.Length); // 4-byte length prefix
        dataWriter.Write(bytes);
    }
}
```

### Reading a Specific Record

```csharp
int targetIndex = 99_999;

using (var indexStream = new FileStream(IndexPath, FileMode.Open, FileAccess.Read))
using (var dataStream  = new FileStream(DataPath,  FileMode.Open, FileAccess.Read))
using (var indexReader = new BinaryReader(indexStream))
using (var dataReader  = new BinaryReader(dataStream))
{
    // Step 1: Seek in the index file to find the data offset
    indexStream.Seek((long)targetIndex * sizeof(long), SeekOrigin.Begin);
    long dataOffset = indexReader.ReadInt64();

    // Step 2: Seek in the data file to that offset
    dataStream.Seek(dataOffset, SeekOrigin.Begin);

    // Step 3: Read the length-prefixed record
    int length     = dataReader.ReadInt32();
    byte[] bytes   = dataReader.ReadBytes(length);
    string record  = Encoding.UTF8.GetString(bytes);

    Console.WriteLine($"Record at index {targetIndex}: {record}");
}
```

---

## Performance Characteristics

| Operation                        | Fixed-Size Records | Variable-Size + Index |
|----------------------------------|--------------------|-----------------------|
| Seek to record                   | O(1)               | O(1) — two seeks      |
| Read single record               | O(1)               | O(1)                  |
| Read entire file sequentially    | O(n)               | O(n)                  |
| Storage overhead                 | Padding bytes      | 8 bytes per record     |
| Implementation complexity        | Low                | Medium                |

`FileStream.Seek()` maps directly to the OS `SetFilePointer` system call on Windows, making it equally fast whether you're jumping to byte 0 or byte 4,800,000,000.

---

## Key Takeaways

- **Fixed-size records** are the simplest approach and require no auxiliary data structures. Pad records to a consistent byte length at write time.
- **Variable-size records** require a companion index file, but add only 8 bytes of overhead per record (one `long` offset).
- Always use `SeekOrigin.Begin` with a calculated offset for deterministic, index-based lookups.
- This pattern is the same fundamental mechanism used by database storage engines for page-based lookups.
