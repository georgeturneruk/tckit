FROM python:3.11-slim

WORKDIR /app

RUN apt-get update && apt-get install -y --no-install-recommends \
    git \
    curl \
    && rm -rf /var/lib/apt/lists/*

COPY pyproject.toml .
COPY tckit/ tckit/

RUN pip install --no-cache-dir -e ".[dev,docs]"

EXPOSE 8000

CMD ["python", "-m", "tckit.server"]
