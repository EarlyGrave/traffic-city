#!/bin/bash
# YS0: матрица замеров (последовательно, по одному тяжёлому прогону). Запуск из корня репозитория.
run() { # scenario threads
  dotnet run --no-build --project src/Sim.Headless -c Release -- bench --scenario "$1" --ready-city --window am_peak --threads "$2" \
    --out "perf/results/ys0/$1-t$2.json" > "perf/results/ys0/$1-t$2.log" 2>&1
}
run ys0-30k 1
run ys0-30k 2
dotnet run --no-build --project src/Sim.Headless -c Release -- ready-city --scenario ys0-50k --days 1 --threads 4 > perf/results/ys0/ys0-50k-ready.log 2>&1
run ys0-50k 4
run ys0-50k 2
run ys0-50k 1
echo done > perf/results/ys0/matrix.done
