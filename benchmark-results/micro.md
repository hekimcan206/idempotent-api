```

BenchmarkDotNet v0.15.8, Windows 11 (10.0.26200.9711/25H2/2025Update/HudsonValley2)
13th Gen Intel Core i7-13620H 2.40GHz, 1 CPU, 16 logical and 10 physical cores
.NET SDK 10.0.401
  [Host]   : .NET 10.0.12 (10.0.12, 10.0.1226.42308), X64 RyuJIT x86-64-v3
  ShortRun : .NET 10.0.12 (10.0.12, 10.0.1226.42308), X64 RyuJIT x86-64-v3

Job=ShortRun  IterationCount=3  LaunchCount=1  
WarmupCount=3  

```
| Method           | Mean          | Error         | StdDev      | Gen0   | Allocated |
|----------------- |--------------:|--------------:|------------:|-------:|----------:|
| ParseKey         |     79.938 ns |     29.060 ns |   1.5929 ns | 0.0197 |     248 B |
| StorageKey       |    335.596 ns |    184.908 ns |  10.1354 ns | 0.0496 |     624 B |
| Fingerprint1KiB  |  1,136.232 ns |  1,106.347 ns |  60.6426 ns | 0.0305 |     400 B |
| Fingerprint64KiB | 32,324.485 ns | 12,807.024 ns | 701.9962 ns |      - |     400 B |
| TokenBucketTake  |      5.927 ns |      7.052 ns |   0.3866 ns |      - |         - |
