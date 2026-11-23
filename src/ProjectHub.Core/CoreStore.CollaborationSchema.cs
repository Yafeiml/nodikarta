namespace ProjectHub.Core;

public sealed partial class CoreStore
{
    private const string CollaborationSchemaSql = """
        CREATE TABLE projecthub.endpoints (
            id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
            project_id uuid NOT NULL REFERENCES projecthub.projects(id),
            device_id uuid NOT NULL REFERENCES projecthub.devices(id),
            binding_id uuid NOT NULL,
            name varchar(120) NOT NULL,
            UNIQUE (binding_id, name)
        );
        CREATE TABLE projecthub.feedback (
            id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
            project_id uuid NOT NULL REFERENCES projecthub.projects(id),
            device_id uuid NOT NULL REFERENCES projecthub.devices(id),
            content text NOT NULL,
            created_at timestamptz NOT NULL DEFAULT clock_timestamp()
        );
        CREATE INDEX feedback_project ON projecthub.feedback(project_id,created_at);
        CREATE TABLE projecthub.analysis_tasks (
            id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
            feedback_id uuid NOT NULL UNIQUE REFERENCES projecthub.feedback(id),
            endpoint_id uuid NOT NULL REFERENCES projecthub.endpoints(id),
            status text NOT NULL DEFAULT 'pending' CHECK (status IN ('pending','claimed','completed')),
            result text,
            CHECK ((status = 'completed') = (result IS NOT NULL))
        );
        CREATE INDEX analysis_target ON projecthub.analysis_tasks(endpoint_id);
        CREATE TABLE projecthub.knowledge_versions (
            commit_id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
            item_id uuid NOT NULL,
            version bigint NOT NULL CHECK (version > 0),
            project_id uuid NOT NULL REFERENCES projecthub.projects(id),
            device_id uuid NOT NULL REFERENCES projecthub.devices(id),
            title varchar(160) NOT NULL,
            body text NOT NULL,
            evidence text NOT NULL,
            related_task_id uuid REFERENCES projecthub.analysis_tasks(id),
            created_at timestamptz NOT NULL DEFAULT clock_timestamp(),
            UNIQUE (project_id,item_id,version)
        );
        CREATE TABLE projecthub.collaboration_requests (
            device_id uuid NOT NULL REFERENCES projecthub.devices(id),
            project_id uuid NOT NULL REFERENCES projecthub.projects(id),
            operation text NOT NULL,
            request_id uuid NOT NULL,
            fingerprint bytea NOT NULL,
            reply jsonb,
            PRIMARY KEY(device_id,project_id,operation,request_id)
        );
        CREATE VIEW projecthub.knowledge_records AS
            SELECT k.*,
                jsonb_build_object('commitId',k.commit_id,'itemId',k.item_id,'version',k.version,
                    'title',k.title,'body',k.body,'evidence',k.evidence,'createdAt',k.created_at,
                    'deviceId',k.device_id,'relatedTaskId',k.related_task_id) AS record
            FROM projecthub.knowledge_versions k;
        """;

    private const string ObservationSchemaSql = """
        ALTER TABLE projecthub.feedback ADD COLUMN observation jsonb;
        UPDATE projecthub.collaboration_requests SET reply=reply || '{"observation":null}'::jsonb
        WHERE operation IN ('feedback.submit','task.claim','task.result') AND reply IS NOT NULL;
        CREATE OR REPLACE VIEW projecthub.task_records AS
            SELECT f.project_id,e.device_id,e.binding_id,t.id,
                jsonb_build_object('taskId',t.id,'feedbackId',f.id,'targetEndpointId',e.id,
                    'content',f.content,'status',t.status,'result',t.result,'observation',f.observation) AS record
            FROM projecthub.analysis_tasks t JOIN projecthub.feedback f ON f.id=t.feedback_id
            JOIN projecthub.endpoints e ON e.id=t.endpoint_id AND e.project_id=f.project_id;
        CREATE OR REPLACE VIEW projecthub.feedback_records AS
            SELECT f.project_id,f.created_at,f.id,
                jsonb_build_object('feedbackId',f.id,'content',f.content,'targetEndpointId',t.endpoint_id,
                    'taskId',t.id,'status',coalesce(t.status,'open'),'result',t.result,'createdAt',f.created_at,
                    'deviceId',f.device_id,'observation',f.observation) AS record
            FROM projecthub.feedback f LEFT JOIN projecthub.analysis_tasks t ON t.feedback_id=f.id;
        """;

    private const string KnowledgeAssetSchemaSql = """
        CREATE TABLE projecthub.knowledge_asset_refs (
            project_id uuid NOT NULL,
            item_id uuid NOT NULL,
            knowledge_version bigint NOT NULL,
            position integer NOT NULL CHECK (position BETWEEN 1 AND 32),
            role text NOT NULL CHECK (role IN ('deliverable','used','evidence')),
            asset_id uuid NOT NULL,
            asset_version integer NOT NULL,
            locator varchar(512),
            PRIMARY KEY (project_id,item_id,knowledge_version,position),
            FOREIGN KEY (project_id,item_id,knowledge_version)
                REFERENCES projecthub.knowledge_versions(project_id,item_id,version),
            FOREIGN KEY (project_id,asset_id,asset_version)
                REFERENCES projecthub.asset_versions(project_id,asset_id,version)
        );
        CREATE UNIQUE INDEX knowledge_one_deliverable
            ON projecthub.knowledge_asset_refs(project_id,item_id,knowledge_version) WHERE role='deliverable';
        CREATE INDEX knowledge_by_deliverable
            ON projecthub.knowledge_asset_refs(project_id,asset_id,asset_version) WHERE role='deliverable';
        CREATE OR REPLACE VIEW projecthub.knowledge_records AS
            SELECT k.*,
                jsonb_build_object('commitId',k.commit_id,'itemId',k.item_id,'version',k.version,
                    'title',k.title,'body',k.body,'evidence',k.evidence,'createdAt',k.created_at,
                    'deviceId',k.device_id,'relatedTaskId',k.related_task_id,'assetRefs',coalesce((
                        SELECT jsonb_agg(jsonb_strip_nulls(jsonb_build_object('role',r.role,'assetId',r.asset_id,
                            'version',r.asset_version,'locator',r.locator)) ORDER BY r.position)
                        FROM projecthub.knowledge_asset_refs r
                        WHERE r.project_id=k.project_id AND r.item_id=k.item_id AND r.knowledge_version=k.version
                    ),'[]'::jsonb)) AS record
            FROM projecthub.knowledge_versions k;
        UPDATE projecthub.collaboration_requests SET reply=reply || '{"assetRefs":[]}'::jsonb
            WHERE operation='knowledge.commit' AND reply IS NOT NULL;
        """;

    private const string KnowledgeValiditySchemaSql = """
        ALTER TABLE projecthub.knowledge_versions
            ADD COLUMN validity jsonb,
            ADD COLUMN supersedes_item_id uuid,
            ADD COLUMN supersedes_version bigint,
            ADD CHECK ((supersedes_item_id IS NULL) = (supersedes_version IS NULL) AND item_id <> supersedes_item_id),
            ADD FOREIGN KEY (project_id,supersedes_item_id,supersedes_version)
                REFERENCES projecthub.knowledge_versions(project_id,item_id,version);
        CREATE UNIQUE INDEX knowledge_successor ON projecthub.knowledge_versions(project_id,supersedes_item_id)
            WHERE supersedes_item_id IS NOT NULL;
        CREATE UNIQUE INDEX knowledge_predecessor ON projecthub.knowledge_versions(project_id,item_id)
            WHERE supersedes_item_id IS NOT NULL;

        ALTER VIEW projecthub.knowledge_records RENAME TO knowledge_record_contents;
        CREATE VIEW projecthub.knowledge_records AS
        SELECT contents.commit_id,contents.item_id,contents.version,contents.project_id,contents.device_id,contents.title,
            contents.body,contents.evidence,contents.related_task_id,contents.created_at,
            contents.record || jsonb_build_object('validity',
                coalesce(k.validity,'{"kind":"legacy"}'::jsonb) || jsonb_strip_nulls(jsonb_build_object(
                    'status',CASE
                        WHEN k.validity IS NULL THEN 'unclassified'
                        WHEN k.validity->>'kind'='fact' THEN 'historical'
                        WHEN k.validity->>'kind'='observation' THEN CASE WHEN k.version=head.version THEN 'lastObserved' ELSE 'historical' END
                        WHEN successor.item_id IS NOT NULL OR k.version<head.version THEN 'superseded'
                        ELSE 'current' END,
                    'supersedesItemId',origin.supersedes_item_id,'supersedesVersion',origin.supersedes_version,
                    'supersededByItemId',coalesce(successor.item_id,CASE WHEN k.validity->>'kind'='decision' AND k.version<head.version THEN k.item_id END),
                    'currentDecision',terminal.decision
                ))) AS record
        FROM projecthub.knowledge_record_contents contents
        JOIN projecthub.knowledge_versions k ON k.commit_id=contents.commit_id
        CROSS JOIN LATERAL (SELECT v.version FROM projecthub.knowledge_versions v WHERE v.project_id=k.project_id
            AND v.item_id=k.item_id ORDER BY v.version DESC LIMIT 1) head
        LEFT JOIN projecthub.knowledge_versions origin ON origin.project_id=k.project_id AND origin.item_id=k.item_id
            AND origin.version<=k.version AND origin.supersedes_item_id IS NOT NULL
        LEFT JOIN projecthub.knowledge_versions successor ON successor.project_id=k.project_id AND successor.supersedes_item_id=k.item_id
        LEFT JOIN LATERAL (
            WITH RECURSIVE chain(item_id) AS (
                SELECT coalesce(successor.item_id,k.item_id)
                WHERE k.validity->>'kind'='decision' AND (successor.item_id IS NOT NULL OR k.version<head.version)
                UNION ALL
                SELECT n.item_id FROM chain c JOIN projecthub.knowledge_versions n
                    ON n.project_id=k.project_id AND n.supersedes_item_id=c.item_id
            )
            SELECT jsonb_build_object('itemId',v.item_id,'version',v.version,'title',v.title,'scope',v.validity->>'scope') AS decision
            FROM chain c JOIN projecthub.knowledge_versions v ON v.project_id=k.project_id AND v.item_id=c.item_id
            WHERE NOT EXISTS (SELECT 1 FROM projecthub.knowledge_versions n WHERE n.project_id=k.project_id AND n.supersedes_item_id=c.item_id)
            ORDER BY v.version DESC LIMIT 1
        ) terminal ON true;
        """;
}
