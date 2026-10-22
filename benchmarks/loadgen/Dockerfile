FROM grafana/k6:latest
WORKDIR /scripts
COPY benchmark.js .
ENTRYPOINT ["k6", "run", "benchmark.js"]
