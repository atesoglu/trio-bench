FROM grafana/k6:latest
WORKDIR /scripts
COPY stress_test.js .
ENTRYPOINT ["k6", "run", "stress_test.js"]
