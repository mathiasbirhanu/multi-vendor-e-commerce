# MarketHub.et — Full Project (Backend + Frontend)

Ethiopian multi-vendor marketplace + first-party store.
Production stack: **.NET 9 modular monolith + PostgreSQL + Next.js 16**.

```
markethub/
├── backend/    .NET 9 API  (6 modules: Catalog, Sales, Identity, Finance, Payments, Notifications)
│               CQRS/MediatR, transactional outbox, JWT, Quartz jobs, double-entry ledger,
│               Meilisearch/SMS/Email adapters (keys from env only), xUnit tests, docker-compose
├── frontend/   Next.js 16 storefront + vendor/admin consoles + Prisma data layer
│               src/server/ = mock API that mirrors the .NET contract 1:1 (runs the preview)
│               prisma/schema.prisma + seed.ts = the same domain model, SQLite for local preview
└── docs/       GAP_PLAN.md (the full feature/endpoint contract both sides implement)
```

## Run the backend (production API)

```bash
cd backend
docker compose up -d                # PostgreSQL + Meilisearch + Mailpit
dotnet ef database update -p src/Modules/MarketHub.Modules.Catalog ...
dotnet run --project src/MarketHub.Api
# Swagger: http://localhost:8080/swagger
```

Full details: `backend/README.md` (also copied to `docs/BACKEND-README.md`).

## Run the frontend (storefront preview)

```bash
cd frontend
cp .env.example .env                # SQLite file DB for the preview
bun install                         # or: npm install
bunx prisma db push                 # or: npx prisma db push
bun run db:reset                    # seed Ethiopian demo data (or: npm run db:reset)
bun run dev                         # or: npm run dev  →  http://localhost:3000
```

With no `NEXT_PUBLIC_API_BASE_URL` set, the app serves data from the built-in mock API in
`src/server/` — the exact same routes/DTOs the .NET API exposes. Point it at a running
backend later without touching a single component.

## Demo accounts (seeded)

| Role | Email | Password |
|---|---|---|
| Admin (SuperAdmin) | admin@markethub.et | Admin@12345 |
| Admin staff (Support) | support@markethub.et | Admin@12345 |
| Vendor (approved) | vendor1@markethub.et | Vendor@12345 |
| Buyer | buyer@markethub.et | Buyer@12345 |

## Companion tutorial

`MarketHub-Complete-Build-Tutorial.pdf` / `.docx` (delivered alongside this zip) walks through
the architecture and every module line by line with the reasoning behind each decision.
# multi-vendor-e-commerce
