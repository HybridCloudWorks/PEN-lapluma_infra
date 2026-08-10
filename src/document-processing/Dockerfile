FROM python:3.13-slim@sha256:9662417aace5ae7b8e2609cce472b72a8958e134ba372808abe9cc1a0c0125e6

ENV PYTHONDONTWRITEBYTECODE=1 \
    PYTHONUNBUFFERED=1 \
    PORT=8080

RUN groupadd --system --gid 10001 lapluma \
    && useradd --system --uid 10001 --gid lapluma --create-home --home-dir /home/lapluma lapluma

WORKDIR /app
COPY contracts.py worker.py ./
USER 10001:10001
EXPOSE 8080
CMD ["python", "worker.py"]
