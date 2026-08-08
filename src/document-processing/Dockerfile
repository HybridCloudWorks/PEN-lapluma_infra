FROM python:3.12-slim@sha256:229a2c5bfa27522db7815ea81f9bed70af17ccb9de9fc7ad142b1877b5830d36

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
