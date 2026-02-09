# Flight Plan: Architecture Intelligence for Platform Teams
---------------------------------------------------------

**Design intent. Explicitly documented.**

Flight Plan is a design‑time architecture and compliance system that makes *what you intend to build* explicit, reviewable, and version-controlled.

It is not a monitoring tool. It does not manage infrastructure. It does not enforce policy.

Flight Plan exists to make system architecture, trust boundaries, and data exposure **explicit, reviewable, and verifiable**.

---

## What Problem Flight Plan Solves

Modern systems fail audits, reviews, and incident retrospectives not because teams lack dashboards — but because **architecture intent lives in slides and heads**, with no single source of truth.

Flight Plan closes that gap by:

* Capturing **design‑time intent** (architecture, trust, data)
* Producing **human‑readable reports** from explicit design documents
* Creating a **reviewable, version-controlled architecture artifact**

The result: fewer surprises, faster reviews, and shared understanding.

---

## The Flight Plan Approach

Flight Plan creates Architecture Intelligence through clear documentation and validation:

### System Blueprint (v1 - Available Now)

* Define the desired architecture of your platform: services, environments, configurations, and policies.
* Serves as the authoritative source of truth for architectural intent.
* Versioned, reviewable, and auditable by design.
* Generate comprehensive reports for architecture, security, and onboarding.
* AI-powered queries for natural language access to architecture information.

**Flight Plan v1 delivers Architecture Intelligence through explicit documentation:**

* **For engineers:** Clear, comprehensive architecture documentation for system understanding and onboarding.
* **For platform teams:** Version-controlled architecture as code with validation and reporting.
* **For leadership and security:** Auditable architecture documentation that serves as the system of record.

### Deployment Intelligence (v2 - Roadmap)

* Ingest evidence from CI/CD pipelines, deployment systems, and runtime environments.
* Reconcile real-world state against the System Blueprint to detect drift and gaps.
* Generate audit-ready reports comparing intent with reality.

Future versions will expand to include deployment reconciliation, providing end-to-end visibility and drift detection across all environments over time.

**Flight Plan is the system of record for platform architecture and reality — ensuring what you design is what actually exists.**

---

## Core Concepts

### Flight Plan (Design‑Time Intent)

A Flight Plan is a YAML document that describes:

* Services and resources
* Trust zones and ownership
* Dependencies and exports
* Data classifications (at rest and in motion)

It answers:

> *What are we intentionally building, and what risks are we accepting?*

---

## What Flight Plan Is (and Is Not)

### Flight Plan *Is*

* A design‑time architecture artifact
* A compliance and trust modeling layer
* A documentation and reporting system for humans *and* machines
* A version-controlled source of truth for system design

### Flight Plan *Is Not*

* A monitoring or observability platform
* A deployment or infrastructure tool
* A policy enforcement engine
* A runtime security scanner

Flight Plan explains systems. It does not run them.

---

## Getting Started

This section walks through the recommended v1 workflow using the Flight Plan CLI.

### Prerequisites

To get value from Flight Plan v1, you need:

* A Flight Plan YAML file describing your system architecture
* The Flight Plan CLI installed locally or in CI

Flight Plan works entirely from your design documents — no runtime access required.

---

### 1. Verify Your Flight Plan

Start by validating your design-time intent:

```bash
flightplan verify flightplan.yml
```

This command checks:

* Schema correctness
* Reference validity (services, resources, zones, data classes)
* Structural consistency

---

### 2. Compile the Flight Plan

Compile the Flight Plan into a machine-readable form:

```bash
flightplan build flightplan.yml
```

This produces a normalized JSON representation used by all downstream steps.

---

### 3. Generate Design-Time Reports

To understand the intended architecture and security posture:

```bash
flightplan report flightplan.yml --type architecture
```

You can generate different design-time reports:

```bash
flightplan report flightplan.yml --type security
flightplan report flightplan.yml --type onboarding
flightplan report flightplan.yml --type service-catalog
```

These reports reflect your documented architecture and design intent.

By default, empty sections and rows are hidden for cleaner reports. To show placeholders for missing data:

```bash
flightplan report flightplan.yml --type architecture --show-empty
```

This displays "Not modeled yet" for optional fields that haven't been filled in, helping identify gaps.

---

### 4. Use the Publish Command (Recommended)

For most workflows, use the single-step publish command:

```bash
flightplan publish flightplan.yml -o ./reports
```

This command:

* Validates the Flight Plan
* Compiles it
* Generates all reports
* Produces a navigable output with a table of contents

The publish output is suitable for:

* CI artifacts
* Architecture reviews
* Audit evidence
* Change records

---

### Typical CI Usage

Flight Plan is designed to run in CI without side effects:

```bash
flightplan publish flightplan.yml -o ./reports --format html
```

Outputs can be archived or attached to pull requests and documentation sites.

---

### AI-Powered Queries (RAG System)

Query your FlightPlan architecture using natural language via Ollama with semantic search:

```bash
# First, build your compiled FlightPlan
flightplan build flightplan.yml -o flightplan.compiled.json

# Build vector index (one-time, ~30 seconds)
flightplan index flightplan.compiled.json

# Query with AI and semantic retrieval
flightplan query flightplan.compiled.json "What services run in production?"
flightplan query flightplan.compiled.json "Tell me about api-gateway-payer"
flightplan query flightplan.compiled.json "Which team owns the SecurityApi service?"
```

**How it works:**
1. The `index` command chunks your FlightPlan and generates embeddings (73 chunks for typical plan)
2. The `query` command finds the most relevant chunks for your question
3. Only relevant context is sent to the AI (2-3KB instead of full 100KB)
4. Results are accurate and fast, with no hallucination

**Prerequisites:**
- Install [Ollama](https://ollama.ai)
- Pull models: `ollama pull nomic-embed-text` and `ollama pull llama3.2`
- Start Ollama: `ollama serve`

See the [RAG Implementation Guide](Docs/RAG-Implementation.md) for details.

---

## CLI Overview

Flight Plan ships as a CLI designed for CI, review workflows, and local iteration.

```text
verify <input>                    Validate Flight Plan only
build <input>                     Compile Flight Plan into JSON
report <input>                    Generate a design-time report
publish <input>                   Generate all artifacts and reports
index <compiled-json>             Build vector index for AI queries (RAG)
query <compiled-json> <question>  Query the FlightPlan using AI with semantic search
```

The `publish` command is the recommended entry point for comprehensive reports.

The `index` and `query` commands enable AI-powered natural language analysis of your FlightPlan using RAG (Retrieval-Augmented Generation). See [RAG Implementation Guide](Docs/RAG-Implementation.md) for details.

---

## Reports Produced in v1

### Architecture Overview Report

**Design‑time system structure**

* Services and resources
* Ownership and platforms
* Dependency graph
* Environment model

Audience: architects, onboarding engineers, reviewers

---

### Security Overview Report

**Design‑time trust and data exposure**

* Trust zones and boundaries
* Data classifications
* Sensitive data in motion vs at rest

Audience: security, compliance, platform teams

---

### Developer Onboarding Report

**Orientation guide for new engineers**

* Where traffic enters (entry points)
* Owners and repo links
* Environments and deployment tooling

Audience: onboarding engineers, operators

---

### Service Catalog Report

**Detailed service inventory**

* Services, repos, owners, zones/platforms
* Exports/interfaces (including consumers)
* Inbound and outbound dependencies

Audience: auditors, platform teams, architecture reviews

---

## The Publish Output

Running `publish` produces a self‑contained output:

| Artifact            | Description                                |
| ------------------- | ------------------------------------------ |
| Compiled Plan       | Machine‑readable compiled Flight Plan      |
| Architecture Report | Design‑time architecture overview          |
| Security Report     | Design‑time security & compliance overview |
| Onboarding Report   | Developer orientation guide                |
| Service Catalog     | Detailed inventory of services and deps    |
| Resource Catalog    | Detailed inventory of resources            |
| Findings            | Aggregated issues and recommendations      |

This output can be archived, reviewed, or attached to change records.

---

## Typical Use Cases

* Architecture and security reviews
* Release readiness and promotion checks
* Audit preparation
* Incident retrospectives
* System onboarding and knowledge transfer

Flight Plan turns implicit assumptions into explicit artifacts.

---

## v1 Scope and Guarantees

Flight Plan v1 guarantees:

* Deterministic outputs from the same inputs
* No runtime side effects
* Clear separation between intent and observation
* Human‑readable reports suitable for review

What v1 intentionally does *not* include:

* Policy enforcement
* Runtime scanning or deployment reconciliation
* Cost or performance analysis
* Alerting or monitoring

---

## Design Philosophy

Flight Plan is built on a simple belief:

> *Systems are safer, faster, and easier to operate when design intent is explicit and documented.*

Flight Plan v1 is the foundation for that clarity.
