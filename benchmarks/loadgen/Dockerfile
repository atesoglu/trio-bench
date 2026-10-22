FROM grafana/k6:latest
WORKDIR /scripts
COPY benchmark_final.js .
ENTRYPOINT ["k6", "run", "benchmark_final.js"]
