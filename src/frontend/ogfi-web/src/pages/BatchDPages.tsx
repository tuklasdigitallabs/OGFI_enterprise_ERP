import {
  Alert,
  Box,
  Button,
  Card,
  CardContent,
  Chip,
  Divider,
  Paper,
  Stack,
  Table,
  TableBody,
  TableCell,
  TableHead,
  TableRow,
  Typography,
} from '@mui/material'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { Link, useParams } from 'react-router'
import { apiClient } from '../api/client'

function asError(error: unknown): Error {
  if (error instanceof Error) return error
  if (typeof error === 'object' && error !== null && 'detail' in error) {
    return new Error(String((error as { detail?: unknown }).detail ?? 'OGFI request failed'))
  }
  return new Error('OGFI request failed')
}

async function requireResult<T>(promise: Promise<{ data?: T; error?: unknown }>): Promise<T> {
  const result = await promise
  if (result.error) throw asError(result.error)
  if (result.data === undefined) throw new Error('OGFI returned no response body.')
  return result.data
}

function Header({ title, description }: { title: string; description: string }) {
  return (
    <Box sx={{ mb: 2 }}>
      <Typography variant="h4" sx={{ fontWeight: 700 }}>{title}</Typography>
      <Typography color="text.secondary">{description}</Typography>
    </Box>
  )
}

export function BatchDOverviewPage() {
  return (
    <>
      <Header
        title="Batch D · Approval Spine"
        description="RI01-BL04 candidate workspace. Workflow orchestrates approval; Procurement remains authoritative for Purchase Order state."
      />
      <Stack direction={{ xs: 'column', lg: 'row' }} spacing={2}>
        <Card variant="outlined" sx={{ flex: 1 }}><CardContent><Typography variant="h6">Versioned Workflow</Typography><Typography variant="body2" color="text.secondary">Every approval instance binds permanently to the Workflow Definition Version selected when it starts.</Typography></CardContent></Card>
        <Card variant="outlined" sx={{ flex: 1 }}><CardContent><Typography variant="h6">Server-authoritative approval</Typography><Typography variant="body2" color="text.secondary">Inbox visibility and decisions are checked against authenticated tenant membership, approval permission, candidate assignment and Outlet scope.</Typography></CardContent></Card>
        <Card variant="outlined" sx={{ flex: 1 }}><CardContent><Typography variant="h6">Procurement-owned outcome</Typography><Typography variant="body2" color="text.secondary">Workflow publishes an immutable outcome. Procurement revalidates the submitted PO revision before applying APPROVED.</Typography></CardContent></Card>
      </Stack>
      <Alert severity="info" sx={{ mt: 3 }}>Batch D is implementation evidence under G9.5. A green build does not itself make this increment RJ-approved.</Alert>
    </>
  )
}

export function ApprovalInboxPage() {
  const inbox = useQuery({
    queryKey: ['approval-inbox'],
    queryFn: () => requireResult(apiClient.GET('/api/workflow/approval-inbox', { params: { query: { limit: 100 } } })),
  })

  return (
    <>
      <Header title="Approval Inbox" description="Only pending tasks assigned to the authenticated, authorized approver and within current organization scope are returned." />
      {inbox.isPending && <Alert severity="info">Loading authoritative approval work…</Alert>}
      {inbox.error && <Alert severity="error">{inbox.error.message}</Alert>}
      <Paper variant="outlined" sx={{ p: 2, overflowX: 'auto' }}>
        {inbox.data && inbox.data.length === 0 && <Alert severity="info">No pending Purchase Order approvals are assigned to you.</Alert>}
        {inbox.data && inbox.data.length > 0 && (
          <Table size="small">
            <TableHead><TableRow><TableCell>Purchase Order</TableCell><TableCell>Business Date</TableCell><TableCell>Total</TableCell><TableCell>Round</TableCell><TableCell>Action</TableCell></TableRow></TableHead>
            <TableBody>
              {inbox.data.map(task => (
                <TableRow key={task.taskId}>
                  <TableCell>{task.purchaseOrderId}</TableCell>
                  <TableCell>{task.businessDate}</TableCell>
                  <TableCell>{task.currency} {task.purchaseOrderTotal.toLocaleString()}</TableCell>
                  <TableCell>{task.approvalRound}</TableCell>
                  <TableCell><Button component={Link} to={`/approvals/${task.taskId}`} size="small">Review</Button></TableCell>
                </TableRow>
              ))}
            </TableBody>
          </Table>
        )}
      </Paper>
    </>
  )
}

export function ApprovalTaskPage() {
  const { taskId = '' } = useParams()
  const queryClient = useQueryClient()
  const task = useQuery({
    queryKey: ['approval-task', taskId],
    enabled: !!taskId,
    queryFn: () => requireResult(apiClient.GET('/api/workflow/tasks/{taskId}', { params: { path: { taskId } } })),
  })
  const approve = useMutation({
    mutationFn: () => requireResult(apiClient.POST('/api/workflow/tasks/{taskId}/approve', { params: { path: { taskId } } })),
    onSuccess: async () => {
      await queryClient.invalidateQueries({ queryKey: ['approval-task', taskId] })
      await queryClient.invalidateQueries({ queryKey: ['approval-inbox'] })
    },
  })

  return (
    <>
      <Header title="Purchase Order Approval" description="The approval action completes the Workflow task only. Procurement applies the PO state change after receiving and revalidating the Workflow outcome." />
      {task.isPending && <Alert severity="info">Loading approval task…</Alert>}
      {task.error && <Alert severity="error">{task.error.message}</Alert>}
      {task.data && (
        <Paper variant="outlined" sx={{ p: 3 }}>
          <Stack direction={{ xs: 'column', md: 'row' }} spacing={2} sx={{ justifyContent: 'space-between', alignItems: { md: 'center' } }}>
            <Box>
              <Typography variant="overline" color="text.secondary">Purchase Order</Typography>
              <Typography variant="h5">{task.data.purchaseOrderId}</Typography>
            </Box>
            <Chip label={task.data.status} />
          </Stack>
          <Divider sx={{ my: 2 }} />
          <Stack direction={{ xs: 'column', md: 'row' }} spacing={4} sx={{ flexWrap: 'wrap' }}>
            <Box><Typography variant="caption" color="text.secondary">Amount</Typography><Typography>{task.data.currency} {task.data.purchaseOrderTotal.toLocaleString()}</Typography></Box>
            <Box><Typography variant="caption" color="text.secondary">Business Date</Typography><Typography>{task.data.businessDate}</Typography></Box>
            <Box><Typography variant="caption" color="text.secondary">Approval Round</Typography><Typography>{task.data.approvalRound}</Typography></Box>
            <Box><Typography variant="caption" color="text.secondary">Subject Revision</Typography><Typography>{task.data.subjectVersion}</Typography></Box>
            <Box><Typography variant="caption" color="text.secondary">Definition Version</Typography><Typography>{task.data.definitionVersion}</Typography></Box>
            <Box><Typography variant="caption" color="text.secondary">Outlet</Typography><Typography>{task.data.outletId}</Typography></Box>
          </Stack>
          <Alert severity="warning" sx={{ mt: 3 }}>Approval is bound to subject revision {task.data.subjectVersion}. If Procurement has moved to a different revision before the outcome is applied, Procurement will reject the result as stale.</Alert>
          {approve.error && <Alert severity="error" sx={{ mt: 2 }}>{approve.error.message}</Alert>}
          {approve.data && <Alert severity="success" sx={{ mt: 2 }}>Workflow decision recorded immutably as {approve.data.decision}. Procurement outcome application remains server-side.</Alert>}
          <Button
            variant="contained"
            sx={{ mt: 3 }}
            disabled={task.data.status !== 'PENDING' || approve.isPending}
            onClick={() => approve.mutate()}
          >
            Approve Purchase Order
          </Button>
        </Paper>
      )}
    </>
  )
}
