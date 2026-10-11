# Glosario de negocio de DocLens (ES → EN)

**Estado:** política de nomenclatura aprobada el 2026-10-11. El esquema de documentos ya está aplicado en los PR #1 de DocLens.Backend (`fix/security-and-pipeline-hardening`) y DocLens.Infra (`fix/governance-deploy-and-bootstrap`). El resto de las filas marcadas **[propuesto]** son orientativas hasta que se implementen.

**Fuentes revisadas** (todas las ramas, solo lectura): DocLens.Backend (`development`, `master`, PR #1), DocLens.Infra (`main`, PR #1), DocLens.Docs (`main`, `claude/doclens-aws-connection-error-icz7aa`: ADR-001…014, `overview.md`, `architecture.md`, `data-flow.md`, `api-reference.md`, `tenant-onboarding.md`, `poc-legislacion-es.md`, `projects/lambda.md`, `governance-plan.md`).

**Contexto jurídico:** despachos de abogados en España (`eu-west-1`, RGPD, BOE, DNI/NIF).

**Leyenda de «Dónde aparece»:** **[código]** en código, SQL o Terraform de alguna rama; **[docs]** solo en documentación; **[propuesto]** no aparece aún en los repositorios (previsto o implicado por el dominio).

## Reglas

1. **Estructura y estado del sistema en inglés.** Van en inglés, en `snake_case` y sin tildes:
   - nombres de tablas, columnas, restricciones e índices, y la tabla de control de migraciones (`schema_migrations`);
   - valores de estado del sistema: escaneo de malware y procesamiento (`PENDING`, `CLEAN`, `PROCESSED`, `REJECTED`), auditoría, permisos, tipos de entidad PII.
2. **Contenido de negocio en español, como dato.** El contenido extraído de los documentos y los valores de dominio que ve el usuario son **datos**, no esquema: se guardan tal cual, en español. Por ejemplo, tipos de documento `contrato`, `escrito`, `prueba`, o áreas del derecho `laboral`, `civil`.
3. **Criterio para decidir si un valor es sistema o dato.** Si el código ramifica o aplica reglas según el valor (máquina de estados, autorización, filtros obligatorios), es **estado del sistema → inglés**. Si es una etiqueta o categoría que el código guarda y muestra sin interpretarla, es **dato → español**. En caso de duda, decidirlo en este glosario.
4. **Código en inglés.** Clases, métodos, variables, parámetros SQL, recursos de Terraform, comentarios y commits van en inglés. Las claves JSON de la extracción con Bedrock también van en inglés; sus valores conservan el idioma original del documento.
5. **Documentación en español.** README, ADR, CLAUDE.md y descripciones de PR van en español. Usan el término de negocio y, la primera vez, el término en código entre comillas invertidas. Ejemplo: «proceso (`matter`)».
6. **Este glosario es la fuente de verdad.** Manda en la traducción dominio ↔ código. Si el código o un documento lo contradice, se corrige el código o se actualiza el glosario en el mismo PR. **Un concepto, un término:** no se usan sinónimos en código.
7. **Uso en prompts.** El glosario puede inyectarse en el prompt de Bedrock como referencia de terminología: términos de negocio en español y claves en inglés. Por eso las tablas mantienen siempre las mismas cinco columnas y sin texto suelto dentro de ellas.
8. **Cómo añadir un término:**
   - Añadir una fila en la sección que corresponda, con las cinco columnas completas.
   - Indicar si el término es esquema o estado (inglés) o dato (español).
   - Si no hay equivalente exacto en inglés, justificar la elección en «Notas/ambigüedad».
   - Marcar el término como **[propuesto]** hasta que exista en código.
   - Si el término toca nombres persistidos, el PR debe incluir la migración en `DocLens.Infra/migrations` y el cambio de código.
   - Si el cambio afecta a Cognito o a la Knowledge Base, el PR debe incluir el plan de recreación.
9. **Excepciones.** Solo se aceptan nombres fijados por servicios externos (`GuardDutyMalwareScanStatus`, el prefijo `custom:` de Cognito, el `field_mapping` de Bedrock). Se documentan aquí, igual que los nombres heredados pendientes de decisión (sección «Decisiones pendientes»).

---

## 1. Entidades y roles del dominio

| Término (ES) | Término en código (EN) | Definición | Dónde aparece | Notas/ambigüedad |
|---|---|---|---|---|
| Despacho (de abogados) | `tenant` | Organización cliente de DocLens. Es la unidad de aislamiento de datos (silo lógico). | [docs] `overview.md`, `architecture.md`, `tenant-onboarding.md`. [código] `TenantMiddleware`, `ITenantContext` | En la documentación de negocio se puede decir «law firm». En código siempre `tenant`. |
| Empresa | `tenant` | En el modelo de datos es el mismo concepto que despacho: tabla `empresas`, columna `empresa_id`. | [docs] ADR-008 (`empresas`, `empresa_id`). [código] antes en `empresa_id`, hoy `tenant_id` | **Ambiguo.** «Empresa» también aparece como empresa cliente del despacho («nombres de empresas/empleadores», ADR-014). Para ese segundo sentido se usa `client` (con tipo persona jurídica) o `organization` en datos PII. No se usa `company` para el tenant. |
| Alta / baja de tenant | `tenant onboarding` / `tenant offboarding` | Provisión y revocación de un despacho. | [docs] `tenant-onboarding.md` | — |
| Slug del tenant | `tenant_id` (valor) | Identificador estable en minúsculas, p. ej. `despacho-acme`. Se usa en claves S3, en la BD y en Cognito. | [docs] `tenant-onboarding.md`. [código] `custom:tenantId` | En ADR-008 conviven `empresas.id` (uuid) y `tenant_slug`. El código guarda el slug en `documents.tenant_id` (text). Propuesta: `tenants.id text PRIMARY KEY` = slug, y `tenant_id` en todas las tablas. |
| Cliente (del despacho) | `client` | Persona física o jurídica a la que el despacho presta servicio. Se asocia a procesos. | [docs] `api-reference.md` (`/clientes`), ADR-008 (`clientes`). [código] `custom:clienteId` | **Colisión técnica:** «client» también designa clientes del SDK de AWS, clientes HTTP y el OAuth `client_id` de Cognito. Se mantiene `client` por ser el término estándar en despachos («customer» suena a comercio). Los objetos técnicos se nombran con su servicio (`s3Client`, `appClientId`). |
| Proceso | `matter` | Asunto o caso legal del despacho: agrupa cliente, documentos, permisos y consultas de IA. Puede ser judicial o no; el ejemplo de la API es un «Contrato de arrendamiento». | [docs] `overview.md`, ADR-008 (`procesos`), `api-reference.md` (`/procesos`), `data-flow.md` (`record Proceso`). [código] aún no hay tabla | **Sin equivalente exacto.** En Derecho español, «proceso» es el procedimiento judicial, pero aquí se usa como «asunto». Elección: `matter`, el término estándar en *legal practice management* para un asunto, judicial o no. Se descarta `case`, que es palabra reservada en C# (`switch … case`) y sugiere solo litigio. Se descarta `process`, que choca con `ProcessDocument`, la Lambda `Processor` y la jerga técnica. Si un asunto tiene procedimiento judicial, se usa `proceedings` (ver sección 8). |
| Expediente | `matter` | Sinónimo de proceso en el lenguaje del despacho (el dossier del asunto). | [docs] solo en `CLAUDE.md` y `README.md` de los PR #1, como glosa de «Procesos» | Sinónimo, no entidad nueva. «Expediente judicial» (*court file*) es otra cosa y no está modelado. |
| Caso | `matter` | Uso coloquial de proceso en la documentación («gestión de casos legales»). | [docs] `overview.md`, `architecture.md` | Sinónimo; no usar `case` en código (ver Proceso). |
| Área del derecho | `practice_area` | Clasificación de un proceso: laboral, divorcio, civil… | [docs] ADR-014 (`area_derecho`, idea futura) | **[propuesto]** «Practice area» es el término de los despachos anglosajones. Los valores (`laboral`, `civil`…) son datos en español (sección 3.2). |
| Usuario | `user` | Persona que accede a DocLens. | [docs] ADR-008 (`usuarios`), `/usuarios`. [código] Cognito | — |
| Usuario interno | `internal user` (tipo `internal`) | Profesional del despacho sin privilegios de administración. | [docs] `tenant-onboarding.md`, `api-reference.md` (valor `interno`) | — |
| Usuario interno admin | tipo `internal_admin` | Usuario interno que crea usuarios y clientes, otorga permisos y consulta la auditoría. | [docs] valor `interno_admin`. [código] comentario en `DocLens.Infra/modules/auth/main.tf` | — |
| Usuario de cliente | tipo `client` | Usuario del cliente del despacho. Solo ve los procesos de su `clienteId`. | [docs] valor `cliente` | — |
| Tipo de usuario | `user_type` | Rol del usuario: `internal_admin`, `internal` o `client`. | [código] Cognito `custom:tipoUsuario` (Infra `modules/auth`). [docs] `usuarios.tipo` | Ver sección 4 para el impacto en Cognito. |
| Abogado / abogada / letrado | `lawyer` | Profesional jurídico del despacho. Hoy no es un rol propio: es un usuario interno. | [docs] `api-reference.md` (ejemplo de email), ADR-011 («¿abogado aprueba?») | En ADR-011, el revisor humano es un abogado. Si se crea un rol, llamarlo `reviewer` (pregunta abierta en ADR-011). |
| Revisor | `reviewer` | Usuario que aprueba o devuelve la extracción de una versión. | [docs] ADR-011 (pregunta abierta) | **[propuesto]** |
| Permiso | `permission` (tabla `matter_permissions`) | Acceso de un usuario a un proceso concreto. | [docs] ADR-008 (`permisos`), `/procesos/{id}/permisos` | Conviene `matter_permissions` para dejar claro el alcance. |
| Nivel (de permiso) | `access_level` | Grado de acceso: `lectura` y, por implicación, escritura. | [docs] ADR-008 (`permisos.nivel`), `api-reference.md` (`"nivel": "lectura"`) | Valores en la sección 3.1. |
| Auditoría / registro de auditoría | `audit log` (tabla `audit_log`) | Registro de quién consultó qué proceso o documento y cuándo. Lo exige el RGPD y el control de accesos. | [docs] ADR-008, `api-reference.md` (`/audit-log`), `governance-plan.md` | Ya en inglés en los docs. |
| Documento | `document` | Archivo de un proceso con historial de versiones (`documentId` estable). | [código] tabla `documents`, `DocumentUploadService`. [docs] ADR-007 | — |
| Versión (de documento) | `document version` | Cada subida de un documento; tiene un número secuencial. | [código] `document_versions`, `DocumentKey`. [docs] ADR-007 | — |
| Subida / URL prefirmada | `upload` / `presigned URL` | PUT directo a S3 con una URL de corta duración. | [código] `POST /api/documents/prepare`. [docs] ADR-005 | — |
| Escaneo (de malware) limpio | `clean scan` | GuardDuty informa `NO_THREATS_FOUND`. | [código] `GuardDutyScanResult`, `ProcessorHandler`. [docs] ADR-004/011 | — |
| Extracción | `extraction` | OCR o parseo más análisis semántico con Bedrock, que producen campos estructurados. | [código] `DocumentExtractionService`. [docs] ADR-003/011 | — |
| Campos extraídos | `extracted_fields` | JSON con los datos extraídos de una versión. | [docs] ADR-007/008 (`campos jsonb`), API (`fields`) | — |
| Diff con la versión previa | `diff_from_previous` | JSON Patch entre versiones. | [docs] ADR-007 (`diff_previo`), API (`diffFromPrevious`) | — |
| Revisión humana | `human review` | Paso bloqueante en el que un abogado aprueba o corrige la extracción. | [docs] ADR-011 (revisión 2026-09-21), `architecture.md` | — |
| Base de conocimiento | `knowledge base` (KB) | Índice vectorial de Bedrock sobre `pgvector`. | [código] Infra `modules/knowledge_base`, `kb.embeddings`. [docs] ADR-001/008 | — |
| Fragmento | `chunk` | Trozo de texto indexado en la KB. | [código] `kb.embeddings.chunk_texto`. [docs] `poc-legislacion-es.md` | — |
| Consulta (a la IA) | `query` | Pregunta en lenguaje natural sobre un proceso; la respuesta cita fuentes. | [docs] `/procesos/{id}/consultas` | **Ambiguo:** «consulta» también significa ver un recurso (`CONSULTA_PROCESO` en la auditoría). Elección: `query` para la IA y `view` para la acción de auditoría. |
| Pregunta / respuesta | `question` / `answer` | Entrada y salida de una consulta de IA. | [docs] `api-reference.md` (`pregunta`, `respuesta`), `RespuestaIA` | — |
| Fuente citada | `cited_source` (`sources` en el API) | Documento y versión que respaldan una respuesta de IA. | [docs] `FuenteCitada`, `fuentes` | — |
| Borrador | `draft` | Primer borrador generado por IA (comunicación o escrito). | [docs] `/procesos/{id}/borradores` | — |
| Instrucciones (de borrador) | `instructions` | Indicaciones del usuario para generar un borrador. | [docs] `api-reference.md` | — |
| Datos personales / PII | `pii` / `personal data` | Información identificativa: nombre, DNI/NIF, dirección, teléfono, email, fecha de nacimiento, IBAN. | [docs] ADR-014 | «PII» es jerga de EE. UU.; en RGPD se dice *personal data*. En código se usa `pii` por brevedad (ya está en `pii_mappings`). |
| Seudonimización / anonimización | `pseudonymisation` / `anonymisation` | Sustitución de datos personales por marcadores reversibles o irreversibles. | [docs] ADR-014 | Ortografía británica, coherente con el RGPD en inglés de la UE. |
| Marcador | `placeholder` | Token que sustituye un dato personal en el texto de la KB, p. ej. `[PARTE_A]`. | [docs] ADR-014 | Valores en la sección 3.1. |
| RGPD | `GDPR` | Reglamento (UE) 2016/679. | [docs] ADR-014, `governance-plan.md` | — |
| Legislación / norma | `legislation` (corpus) / `legal_act` (unidad) | Normas del BOE importadas como contenido compartido de la KB. | [docs] `poc-legislacion-es.md` | **[propuesto]** «Norma» abarca Constitución, leyes y reglamentos. `statute` se descarta porque excluye la Constitución y los reglamentos; `legal_act` sigue el uso de la UE. |
| Artículo / sección | `article` / `section` | Unidad de cita de una norma. | [docs] `poc-legislacion-es.md` | **[propuesto]** |
| Texto consolidado | `consolidated text` | Versión del BOE que integra las modificaciones; tiene carácter informativo. | [docs] `poc-legislacion-es.md` | — |
| Vigencia / vigente | `effective_date` (contrato) / `in_force` (norma) | Fecha desde la que rige un contrato, o si una norma rige en una fecha dada. | [código] prompt `Contract`: `effective_date`. [docs] API (`fecha_vigencia`), `poc-legislacion-es.md` | **Ambiguo:** para contratos es una fecha; para normas es un estado temporal. Se usan dos términos distintos. |

## 2. Esquema persistido

### 2.1 Aplicado (DocLens.Infra `migrations/002_documents.sql`, PR #1)

| Término (ES) | Término en código (EN) | Definición | Dónde aparece | Notas/ambigüedad |
|---|---|---|---|---|
| Documentos (antes `documentos`) | `documents` | Tabla: un documento por fila. | [código] `002_documents.sql`, `AuroraDocumentRepository.cs` | Fichero renombrado de `002_documentos.sql`. |
| Identificador | `documents.id` | PK uuid. | ídem | — |
| Tenant (antes `empresa_id`) | `documents.tenant_id` | Slug del tenant (claim `custom:tenantId`); filtro obligatorio en toda consulta. | ídem; índice `documents_tenant_id_idx` | Se alinea con `custom:tenantId` y con `ITenantContext.TenantId`. |
| Última versión | `documents.latest_version` | Último número de versión asignado, con incremento atómico. | ídem; CHECK `documents_latest_version_check` | Sin cambio. |
| Creado en / actualizado en (antes `creado_en` / `actualizado_en`) | `created_at` / `updated_at` | Marcas de tiempo. | `documents`, `document_versions` | — |
| Versiones de documento (antes `documento_versiones`) | `document_versions` | Tabla: una fila por versión subida. | [código] `002_documents.sql`, `AuroraDocumentRepository.cs` | — |
| Documento (antes `documento_id`) | `document_versions.document_id` | FK a `documents.id` (ON DELETE CASCADE). | FK `document_versions_document_id_fkey` | — |
| Número de versión (antes `version_numero`) | `document_versions.version_number` | Número ≥ 1, único por documento. | CHECK `document_versions_version_number_check`, UNIQUE `document_versions_document_id_version_number_uq` | Coincide con `versionNumber` del API. |
| Clave S3 | `document_versions.s3_key` | Clave del objeto en S3. | UNIQUE `document_versions_s3_key_uq` | Sin cambio. |
| Tipo de contenido | `document_versions.content_type` | MIME validado. | ídem | Sin cambio. |
| Estado (antes `estado`) | `document_versions.status` | Estado del sistema (sección 3.1). | CHECK `document_versions_status_check` | — |
| Control de migraciones | `public.schema_migrations` (`filename`, `applied_at`) | Ficheros aplicados por `apply.py`. | [código] `migrations/apply.py` | Ya en inglés. |
| Renombrado opcional | `migrations/optional/rename_spanish_tables.sql` | Renombra tablas en español creadas a mano (con sus datos) al esquema de `002_documents.sql`. | [código] PR #1 de Infra | Manual: `apply.py` no lo ejecuta. Idempotente. |
| Tabla vectorial de la KB | `kb.embeddings` (`id`, `embedding`, `chunk_texto`, `metadata`) | Almacén vectorial de Bedrock Knowledge Bases. | [código] `003_kb_embeddings.sql`, Infra `modules/knowledge_base` (`field_mapping`) | **Excepción pendiente.** `chunk_texto` debería ser `chunk_text`, pero la KB de sbx ya existe con ese nombre; cambiarlo obliga a recrearla. |
| Base de datos | `doclens` | Nombre de la BD Aurora. | [código] Infra `modules/data/main.tf` | Nombre de producto. |

### 2.2 Previsto en ADR-007, ADR-008 y ADR-014 (aún no existe en la BD)

| Término (ES) | Término en código (EN) | Definición | Dónde aparece | Notas/ambigüedad |
|---|---|---|---|---|
| `empresas` (`id`, `nombre`, `tenant_slug`, `creado_en`) | `tenants` (`id`, `name`, `created_at`) | Un despacho por fila. | [docs] ADR-008 | **[propuesto]** `id text` = slug. Así sobra `tenant_slug` y `tenant_id` es FK directa. |
| `usuarios` (`id`, `empresa_id`, `tipo`, `cliente_id`, `email`, `creado_en`) | `users` (`id`, `tenant_id`, `user_type`, `client_id`, `email`, `created_at`) | Usuarios internos y de cliente. | [docs] ADR-008 | **[propuesto]** `client_id` es NULL salvo si `user_type = 'client'`. |
| `clientes` (`id`, `empresa_id`, `nombre`, `creado_en`) | `clients` (`id`, `tenant_id`, `name`, `created_at`) | Clientes del despacho. | [docs] ADR-008 | **[propuesto]** |
| `procesos` (`id`, `empresa_id`, `cliente_id`, `titulo`, `estado`, `creado_en`, `actualizado_en`) | `matters` (`id`, `tenant_id`, `client_id`, `title`, `status`, `created_at`, `updated_at`) | Asuntos o casos. | [docs] ADR-008, `data-flow.md` | **[propuesto]** Ver «Proceso» en la sección 1. |
| Proceso del documento (`proceso_id`) | `documents.matter_id` | FK al proceso. | [docs] ADR-007/008 | **[propuesto]** Hoy no existe en `002_documentos.sql`. |
| Tipo de documento (`tipo`) | `documents.document_type` | Categoría del documento. La columna va en inglés; el valor es dato en español (sección 6). | [docs] ADR-007/008 | **[propuesto]** |
| Hash (`sha256`) | `sha256` | Hash del contenido, para detectar duplicados. | [docs] ADR-007/008 | **[propuesto]** |
| Campos extraídos (`campos`) | `extracted_fields` (jsonb) | Campos extraídos. | [docs] ADR-007/008 | **[propuesto]** Columna en inglés; claves JSON en inglés y valores en el idioma original del documento (regla 4). |
| Diff previo (`diff_previo`) | `diff_from_previous` (jsonb) | JSON Patch. | [docs] ADR-007/008 | **[propuesto]** |
| Procesado en (`procesado_en`) | `processed_at` | Fin de la extracción. | [docs] ADR-008 | **[propuesto]** |
| `permisos` (`id`, `usuario_id`, `proceso_id`, `nivel`) | `matter_permissions` (`id`, `user_id`, `matter_id`, `access_level`) | Acceso usuario ↔ proceso. | [docs] ADR-008 | **[propuesto]** Añadir `tenant_id` para mantener el filtro obligatorio. |
| `audit_log` (`id`, `empresa_id`, `usuario_id`, `accion`, `recurso`, `resultado`, `creado_en`) | `audit_log` (`id`, `tenant_id`, `user_id`, `action`, `resource`, `outcome`, `created_at`) | Auditoría de accesos. | [docs] ADR-008, `api-reference.md` | **[propuesto]** Se prefiere `outcome` a `result`, que choca con `Result<T>` y similares. |
| `pii_mappings` (`id`, `documento_id`, `placeholder`, `valor_real`, `tipo_entidad`, `creado_en`) | `pii_mappings` (`id`, `document_id`, `placeholder`, `original_value`, `entity_type`, `created_at`) | Mapeo reversible marcador → dato real. | [docs] ADR-014 | **[propuesto]** Añadir `tenant_id`; ver la pregunta de cifrado por columna en ADR-014. |

## 3. Valores persistidos

### 3.1 Estados del sistema (inglés)

| Término (ES) | Término en código (EN) | Definición | Dónde aparece | Notas/ambigüedad |
|---|---|---|---|---|
| Pendiente | `PENDING` | URL prefirmada emitida; archivo no subido o no escaneado. | [código] `DocumentVersionStatus.Pending`, CHECK `document_versions_status_check` | Aplicado. |
| Limpio | `CLEAN` | GuardDuty no encontró amenazas; desbloquea la extracción. | [código] `DocumentVersionStatus.Clean`, `ProcessorHandler` | Aplicado. |
| Procesado | `PROCESSED` | Extracción terminada. | [código] `DocumentVersionStatus.Processed` | Aplicado. ADR-007 lo llama `COMPLETED`, y la revisión de ADR-011 lo sustituye por `PENDING_REVIEW` → `APPROVED`/`NEEDS_CORRECTION` → `INDEXED` (más `DUPLICATE`). **Rediseño pendiente.** |
| Rechazado | `REJECTED` | GuardDuty devolvió `THREATS_FOUND` o `UNSCANNABLE`. | [código] `DocumentVersionStatus.Rejected` (aún sin conectar) | No confundir con el rechazo del revisor (`NEEDS_CORRECTION`, ADR-011). |
| Pendiente de revisión / aprobado / requiere corrección / indexado / duplicado | `PENDING_REVIEW` / `APPROVED` / `NEEDS_CORRECTION` / `INDEXED` / `DUPLICATE` | Estados futuros del ciclo de ADR-011 y ADR-007. | [docs] ADR-007/011, `architecture.md` | **[propuesto]** Rediseño pendiente; no están en la restricción CHECK. |
| Estado del proceso: abierto / cerrado | `OPEN` / `CLOSED` | Ciclo de vida de un proceso. | [docs] `api-reference.md` (`"estado": "ABIERTO"`) | **[propuesto]** Estado del sistema según la regla 3 (el código aplica transiciones). |
| Nivel de permiso: lectura / escritura | `READ` / `WRITE` | Alcance del acceso a un proceso. | [docs] `api-reference.md` (`"nivel": "lectura"`) | **[propuesto]** La autorización depende de él → sistema. |
| Acción de auditoría «consulta de proceso» | `MATTER_VIEWED` | Evento de auditoría. | [docs] `api-reference.md` (`CONSULTA_PROCESO`) | **[propuesto]** Formato `<ENTIDAD>_<VERBO_PASADO>`. |
| Resultado de auditoría: permitido / denegado | `ALLOWED` / `DENIED` | Resultado del control de acceso. | [docs] `api-reference.md` (`PERMITIDO`). [código] `DocumentAccessDecision` | **[propuesto]** |
| Tipos de entidad PII | `FULL_NAME`, `NATIONAL_ID`, `TAX_ID`, `ADDRESS`, `PHONE`, `EMAIL`, `DATE_OF_BIRTH`, `IBAN` | Valores de `pii_mappings.entity_type`. | [docs] ADR-014 | **[propuesto]** DNI/NIE → `NATIONAL_ID`; NIF de persona jurídica / CIF → `TAX_ID`. Para personas físicas NIF = DNI: no hay equivalente exacto. |
| Marcadores de PII `[PARTE_A]`, `[FECHA_1]`, `[DIRECCIÓN_1]` | `[PARTY_A]`, `[DATE_1]`, `[ADDRESS_1]` | Tokens que sustituyen datos personales en el texto de la KB. | [docs] ADR-014 | **[propuesto]** Son tokens del sistema (se resuelven con `pii_mappings`), no contenido. Validar en la PoC que no degradan al LLM en prosa española. |
| Tipos de usuario `interno_admin` / `interno` / `cliente` | (pendiente) `internal_admin` / `internal` / `client` | Valores de `custom:tipoUsuario`. | [docs] `tenant-onboarding.md`, ADR-008. [código] comentario en Infra `modules/auth/main.tf` | **Decisión pendiente** junto con los atributos de Cognito (sección 4). Por la regla 3 serían sistema (la autorización depende de ellos). |

### 3.2 Valores de dominio (español, como datos)

| Término (ES) | Término en código (EN) | Definición | Dónde aparece | Notas/ambigüedad |
|---|---|---|---|---|
| Tipo de documento: `contrato`, `escrito`, `prueba`, `comunicación`, `otro` | columna/campo `document_type`; valores sin traducir | Categoría del documento que elige o ve el usuario. | [docs] `api-reference.md` (`tipoDocumento`, texto libre), ADR-005/007 | Dato: se guarda en español. El enum C# `DocumentType` (`Invoice`, `Contract`, `Report`, `Cv`) es heredado; su rediseño (enum o texto libre, y el mapeo a estos valores) está **pendiente**. |
| Tipo de borrador: `carta_requerimiento` | campo `draft_type`; valor sin traducir | Plantilla de borrador elegida por el usuario. | [docs] `api-reference.md` (`/borradores`) | Dato en español. |
| Área del derecho: `laboral`, `civil`, `divorcio`… | columna `practice_area`; valores sin traducir | Clasificación de un proceso. | [docs] ADR-014 (`area_derecho`) | **[propuesto]** Dato. «Divorcio» es una materia, no un área: depurar la lista. |
| Contenido extraído (valores de `extracted_fields`) | claves EN (`parties`, `effective_date`…); valores en el idioma del documento | Datos extraídos por Bedrock. | [código] `BedrockSemanticAnalysisService.BuildPrompt` (instrucción: claves en inglés, valores sin traducir) | Regla 4. |
| Títulos, nombres, preguntas, respuestas, borradores | `title`, `name`, `question`, `answer`, `draft` | Texto libre del usuario o generado por IA. | [docs] `api-reference.md` | Dato. |

## 4. Atributos de Cognito

| Término (ES) | Término en código (EN) | Definición | Dónde aparece | Notas/ambigüedad |
|---|---|---|---|---|
| `custom:tenantId` | `custom:tenantId` | Slug del tenant en el ID token; base del aislamiento. | [código] Infra `modules/auth/main.tf`, `TenantMiddleware`. [docs] `tenant-onboarding.md` | Ya en inglés; sin cambio. Así desaparece la decisión pendiente de renombrarlo. |
| `custom:tipoUsuario` | `custom:userType` | Tipo de usuario (sección 3). | [código] Infra `modules/auth/main.tf`. [docs] `tenant-onboarding.md` | **Coste:** Cognito no permite renombrar ni borrar atributos personalizados. Hay dos opciones: (a) añadir `userType`, migrar los valores y dejar `tipoUsuario` obsoleto en el pool; (b) recrear el User Pool, lo que obliga a recrear usuarios y cambia el `user_pool_id` y el `client_id`. Para sbx (pool `eu-west-1_h6nZdU3S7`) se recomienda (b) si no tiene usuarios reales. |
| `custom:clienteId` | `custom:clientId` | Cliente del despacho al que pertenece un usuario de tipo `client`. | ídem | Mismo coste que `tipoUsuario`. **Colisión:** puede confundirse con el OAuth `client_id` (App Client) presente en los tokens de Cognito. Alternativa: `custom:firmClientId`. Elección propuesta: `clientId`, documentando que nunca es el App Client. |

## 5. S3 y metadatos de la Knowledge Base

| Término (ES) | Término en código (EN) | Definición | Dónde aparece | Notas/ambigüedad |
|---|---|---|---|---|
| Clave S3 de versión | `{tenantId}/documents/{documentId}/v{versionNumber}.{ext}` | Ruta del objeto original. | [código] `Models/DocumentKey.cs`. [docs] ADR-005/007, `data-flow.md` | Ya en inglés; sin cambio. Hay objetos en sbx con este prefijo desde el 2026-09-12. |
| Archivo de metadatos | `{…}.{ext}.metadata.json` → `metadataAttributes` | Metadatos por documento para el filtro `Retrieve` de la KB. | [docs] `data-flow.md`, ADR-007 | — |
| `empresa_id` / `proceso_id` / `documento_id` (atributos de metadatos) | `tenant_id` / `matter_id` / `document_id` | Filtros obligatorios en cada `Retrieve`. | [docs] ADR-001/012, `architecture.md`, `data-flow.md`, `tenant-onboarding.md` (baja de tenant) | **[propuesto]** Aún no hay ingesta en código. Cambiarlos más tarde exige reingestar, así que conviene fijarlos ya en inglés. |
| Metadatos de legislación (`boe_id`, `titulo`, `articulo_o_seccion`, `source_url`, `publication_date`, `last_updated`, `corpus_commit`) | `boe_id`, `title`, `article_or_section`, `source_url`, `publication_date`, `last_updated`, `corpus_commit` | Procedencia de cada fragmento legislativo. | [docs] `poc-legislacion-es.md` | **[propuesto]** `boe_id` se mantiene: el BOE es nombre propio. |
| Buckets `doclens-documents-{env}` / `doclens-kb-source-{env}` | sin cambios | Originales con PII / texto redactado para la KB. | [código] Infra `modules/documents`. [docs] ADR-011/014 | Ya en inglés. |
| Etiqueta `GuardDutyMalwareScanStatus` | sin cambios | Etiqueta del resultado del escaneo, puesta por AWS. | [docs] ADR-004/011 | Nombre fijado por AWS. |
| Metadatos de objeto S3 (`x-amz-meta-*`) | — | El código no fija ninguno; el PUT prefirmado solo lleva `Content-Type`. | [código] `DocumentUploadService.cs` | Nada que renombrar. |

## 6. Tipos de documento y campos extraídos

| Término (ES) | Término en código (EN) | Definición | Dónde aparece | Notas/ambigüedad |
|---|---|---|---|---|
| Contrato | valor `contrato`; C# `DocumentType.Contract` | Acuerdo entre partes. | [código] `DocumentType.Contract`. [docs] ADR-005/007, API | El valor persistido es dato en español (sección 3.2). El identificador C# va en inglés. |
| Escrito | valor `escrito`; C# `Filing` (propuesto) | Documento procesal presentado ante un órgano judicial (demanda, contestación, recurso…). | [docs] ADR-003/005/007, API | **Sin equivalente exacto.** *Pleading* es de *common law*; *brief* es de EE. UU. Se elige `Filing` para el identificador de código. |
| Prueba | valor `prueba`; C# `Evidence` (propuesto) | Documento aportado como medio de prueba. | [docs] ADR-005/007, API | — |
| Comunicación | valor `comunicación`; C# `Correspondence` (propuesto) | Cartas, emails y burofaxes con clientes o terceros. | [docs] ADR-005/007, API | ¿El valor lleva tilde? La regla de «sin tildes» aplica al esquema, no a los datos; aun así, conviene un valor normalizado (`comunicacion`) si se usa como clave de filtro. Decidir en el rediseño de `DocumentType`. |
| Otro | valor `otro`; C# `Other` (propuesto) | Categoría residual. | [docs] ADR-007 | — |
| Factura / informe / CV | C# `Invoice` / `Report` / `Cv` | Tipos heredados del enum actual. | [código] `Models/DocumentType.cs` | Heredados y genéricos; rediseño **pendiente**. «Dictamen» (*legal opinion*) sería el tipo legal, **[propuesto]**. |
| Carta de requerimiento | valor `carta_requerimiento`; C# `DemandLetter` (propuesto) | Requerimiento formal extrajudicial (p. ej. de pago). | [docs] API (`/borradores`) | **Sin equivalente exacto.** «Requerimiento» también puede ser judicial o notarial. |
| Partes | clave `parties` | Personas o entidades que intervienen. | [código] prompt `Contract`. [docs] API (`partes`) | En litigio: `claimant` / `defendant` (sección 8). |
| Fecha de vigencia | clave `effective_date` | Inicio de efectos de un contrato. | [código] prompt `Contract`. [docs] API (`fecha_vigencia`) | «Vigente», aplicado a normas, es `in_force` (sección 1). |
| Fecha de vencimiento | clave `expiry_date` | Fin de la vigencia de un contrato. | [código] prompt `Contract`. [docs] API (`fecha_vencimiento`) | No confundir con plazo procesal (`deadline`). |
| Ley aplicable | clave `governing_law` | Ley que rige el contrato. | [código] prompt `Contract` | — |
| Resumen de obligaciones | clave `key_obligations_summary` | Obligaciones principales del contrato. | [código] prompt `Contract` | — |

## 7. API y contratos de código con nombres en español (en docs)

Los campos y rutas del API son contrato de código y siguen la regla. Hoy el código solo implementa `/api/documents/prepare` y `/api/documents/process`, ya en inglés. El resto de esta sección sale de `api-reference.md` y `data-flow.md`, y es **[propuesto]** hasta que se implemente.

| Término (ES) | Término en código (EN) | Definición | Dónde aparece | Notas/ambigüedad |
|---|---|---|---|---|
| `/procesos`, `/procesos/{procesoId}` | `/matters`, `/matters/{matterId}` | CRUD de procesos. | [docs] `api-reference.md` | — |
| `/procesos/{id}/documentos` | `/matters/{matterId}/documents` | Documentos de un proceso. | ídem | — |
| `/procesos/{id}/permisos` | `/matters/{matterId}/permissions` | Otorgar acceso. | ídem | — |
| `/procesos/{id}/consultas` | `/matters/{matterId}/queries` | Consulta de IA. | ídem | — |
| `/procesos/{id}/borradores` | `/matters/{matterId}/drafts` | Borrador de IA. | ídem | — |
| `/clientes`, `/clientes/{clienteId}/usuarios`, `/clientes/{id}/procesos/{id}` | `/clients`, `/clients/{clientId}/users`, `/clients/{clientId}/matters/{matterId}` | Clientes y su asociación. | ídem | — |
| `/usuarios`, `/usuarios/{usuarioId}` | `/users`, `/users/{userId}` | Usuarios internos. | ídem | — |
| Campos `procesoId`, `clienteId`, `empresaId`, `usuarioId`, `tipoDocumento`, `titulo`, `nombre`, `estado`, `nivel`, `tipo`, `creadoEn`, `actualizadoEn` | `matterId`, `clientId`, `tenantId`, `userId`, `documentType`, `title`, `name`, `status`, `accessLevel`, `userType`, `createdAt`, `updatedAt` | Campos JSON del API. | ídem | — |
| Campos `pregunta`, `respuesta`, `fuentes`, `borrador`, `instrucciones`, `entradas`, `accion`, `recurso`, `resultado` | `question`, `answer`, `sources`, `draft`, `instructions`, `entries`, `action`, `resource`, `outcome` | Campos JSON de IA y auditoría. | ídem | — |
| `record Proceso`, `Cliente`, `Documento`, `DocumentoVersion`, `RespuestaIA`, `FuenteCitada` | `Matter`, `Client`, `Document`, `DocumentVersion`, `AiAnswer`, `CitedSource` | Contratos C# de los docs. | [docs] `data-flow.md` | Los propios contratos ya mezclan idiomas (`LatestVersion` junto a `VersionNumero`). |
| `ClienteService`, `DocumentoService`, `UsuarioService`, `MapClienteEndpoints` | `ClientService`, `DocumentService`, `UserService`, `MapClientEndpoints` | Servicios descritos en docs. | [docs] `projects/lambda.md` | El código real ya usa `DocumentUploadService` y similares. |

## 8. Términos jurídicos españoles sin equivalente exacto (referencia, no presentes en código)

Ninguno de estos términos aparece en los repositorios con sentido jurídico. «Demanda» solo aparece como «bajo demanda», en sentido técnico, y «sentencias» solo como sentencias SQL. Se fijan ahora para que los futuros tipos de documento o de evento no improvisen traducciones. Todos son **[propuesto]**.

| Término (ES) | Término en código (EN) | Definición | Dónde aparece | Notas/ambigüedad |
|---|---|---|---|---|
| Procedimiento / proceso judicial | `proceedings` | Procedimiento ante un órgano judicial dentro de un asunto. | [propuesto] | Un `matter` puede tener cero o más `proceedings`. Esto resuelve la ambigüedad de «proceso» en el Derecho español. |
| Número de procedimiento / autos (y NIG) | `proceedings_number` (y `nig`) | Identificador del procedimiento en el juzgado. | [propuesto] | El NIG (Número de Identificación General) no tiene equivalente; se mantiene como `nig`, nombre propio. |
| Demanda | `statement_of_claim` (tipo de escrito) / `claim` | Escrito que inicia un procedimiento civil. | [propuesto] | **Sin equivalente exacto.** *Complaint* es de EE. UU. Se elige `statement_of_claim`, terminología de Inglaterra y Gales, más próxima al sistema continental. «Demanda» es un subtipo de `filing`. |
| Contestación a la demanda | `statement_of_defence` | Respuesta del demandado. | [propuesto] | Par de `statement_of_claim`. |
| Demandante / demandado | `claimant` / `defendant` | Partes del procedimiento civil. | [propuesto] | En lo penal, «acusado» sería `accused`; fuera de alcance. |
| Procurador (de los tribunales) | `court_representative` | Representante procesal de la parte ante el juzgado, distinto del abogado. | [propuesto] | **Sin equivalente.** *Solicitor* y *attorney* inducen a error porque designan al abogado. Se elige el descriptivo `court_representative` y se documenta como «procurador». |
| Auto | `court_order` | Resolución judicial motivada sobre cuestiones incidentales o de fondo no definitivas. | [propuesto] | **Sin equivalente exacto.** «Autos», en plural, también significa el expediente judicial. Hay que distinguirlo de otras resoluciones: sentencia (`judgment`), providencia (`procedural_order`) y decreto del LAJ (`clerk_decree`). |
| Sentencia | `judgment` | Resolución que pone fin a la instancia. | [propuesto] | — |
| Providencia | `procedural_order` | Resolución de mero trámite. | [propuesto] | — |
| Recurso | `appeal` | Impugnación de una resolución. | [propuesto] | «Recurso» también es recurso de AWS en los docs; en código de dominio, siempre `appeal`. |
| Juzgado / tribunal | `court` | Órgano judicial. | [propuesto] | — |
| Plazo (procesal) | `deadline` | Término para un acto procesal, en días hábiles. | [propuesto] | Distinto de `expiry_date` de contrato. Si se modelan, guardar si el plazo cuenta días hábiles o naturales. |
| Notificación judicial (LexNET) | `court_notification` | Comunicación del juzgado al profesional. | [propuesto] | LexNET es nombre propio. |

---

---

## Decisiones pendientes

1. **Cognito:** `custom:tipoUsuario` y `custom:clienteId` (y sus valores `interno_admin`/`interno`/`cliente`). Cognito no permite renombrar ni borrar atributos personalizados. Las opciones son añadir atributos nuevos y migrar a los usuarios, o recrear el User Pool. `custom:tenantId` ya está en inglés.
2. **`kb.embeddings.chunk_texto` → `chunk_text`:** exige migrar la tabla y recrear la Knowledge Base de sbx.
3. **Enum `DocumentType`:** elegir entre enum cerrado o texto libre, decidir el mapeo a los valores de dominio en español (`contrato`, `escrito`…) y el destino de `Invoice`/`Report`/`Cv`.
4. **Estados de ADR-011:** decidir si se adopta `PENDING_REVIEW` → `APPROVED`/`NEEDS_CORRECTION` → `INDEXED` (y `DUPLICATE`) en lugar de `PROCESSED`/`COMPLETED`.
5. **Tablas en sbx:** comprobar `SELECT to_regclass('public.documentos');`. Si existen tablas creadas a mano en español, ejecutar `migrations/optional/rename_spanish_tables.sql` antes de `apply.py`.
6. **DocLens.Docs:** actualizar ADR-005/007/008/014, `data-flow.md` y `api-reference.md` a los nombres de esquema en inglés.
