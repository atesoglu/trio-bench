FROM docker.io/grafana/k6:2.3.0
WORKDIR /scripts
COPY benchmark.js .
ENTRYPOINT ["k6", "run", "benchmark.js"]
