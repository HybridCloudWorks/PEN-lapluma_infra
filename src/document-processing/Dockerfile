FROM python:3.13-slim@sha256:7ce4b6dfe35e55397b7cda544f8a13f191b7ae28dc5aad71fe664dbc9bc2623f

ENV PYTHONDONTWRITEBYTECODE=1 \
    PYTHONUNBUFFERED=1 \
    PORT=8080

# The worker is stdlib-only, so the image ships no installer: nothing here ever runs pip, and the
# packaging toolchain is the one part of the base image that keeps accruing scanner findings
# (setuptools itself, and the libraries pip vendors, msgpack among them). Removing it in the zone
# that parses hostile documents deletes the findings at their root instead of chasing versions.
# msgpack/setuptools/wheel first — pip exits zero for ones that are absent — then pip removes
# itself.
RUN python -m pip uninstall --yes --disable-pip-version-check msgpack setuptools wheel \
    && python -m pip uninstall --yes --disable-pip-version-check pip

RUN groupadd --system --gid 10001 lapluma \
    && useradd --system --uid 10001 --gid lapluma --create-home --home-dir /home/lapluma lapluma

WORKDIR /app
COPY contracts.py worker.py ./
USER 10001:10001
EXPOSE 8080
CMD ["python", "worker.py"]
