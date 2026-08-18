import {
  Alert,
  Box,
  Button,
  Card,
  CardContent,
  Chip,
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

function SectionHeader({ title, description }: { title: string; description: string }) {
  return <Box sx={{ mb: 2 }}><Typography variant="h4" sx={{ fontWeight: 700 }}>{title}</Typography><Typography color="text.secondary">{description}</Typography></Box>
}

function LoadingOrError({ pending, error }: { pending: boolean; error: Error | null }) {
  if (pending) return <Alert severity="info">Loading authoritative Finance state…</Alert>
  if (error) return <Alert severity="error">{error.message}</Alert>
  return null
}

export function BatchFOverviewPage() {
  const cards = [
    ['Finance-owned mapping', 'Goods Receipt events carry commercial facts. Effective Finance Posting Rules select stable debit and credit Accounts.'],
    ['Balanced Journal', 'Every posted Journal uses exact debit and credit values and is rejected unless the entry balances.'],
    ['Independent fan-out', 'Inventory and Finance acknowledge the same immutable event through separately owned durable consumer-delivery state.'],
    ['Visible recovery', 'Finance Source Posting exposes PENDING, POSTED or FAILED state and replays the same immutable source identity after remediation.'],
  ]
  return <>
    <SectionHeader title="Batch F · Financial Consequence" description="RI01-BL06 candidate workspace. Finance owns accounting truth; Batch F remains unaccepted until controlled G9.7 validation and explicit RJ approval." />
    <Stack direction={{ xs: 'column', lg: 'row' }} spacing={2} sx={{ flexWrap: 'wrap' }}>
      {cards.map(([title, body]) => <Card key={title} variant="outlined" sx={{ flex: '1 1 280px' }}><CardContent><Typography variant="h6" gutterBottom>{title}</Typography><Typography variant="body2" color="text.secondary">{body}</Typography></CardContent></Card>)}
    </Stack>
    <Alert severity="info" sx={{ mt: 3 }}>The UI displays server-authoritative Finance results only. It does not choose GL accounts, move Business Dates or calculate Journal truth locally.</Alert>
  </>
}

export function FinanceSourcePostingsPage() {
  const queryClient = useQueryClient()
  const postings = useQuery({
    queryKey: ['finance-source-postings'],
    queryFn: () => requireResult(apiClient.GET('/api/finance/source-postings', { params: { query: { limit: 100 } } })),
  })
  const replay = useMutation({
    mutationFn: (sourcePostingId: string) => requireResult(apiClient.POST('/api/finance/source-postings/{sourcePostingId}/replay', { params: { path: { sourcePostingId } } })),
    onSuccess: async () => {
      await queryClient.invalidateQueries({ queryKey: ['finance-source-postings'] })
      await queryClient.invalidateQueries({ queryKey: ['finance-journals'] })
    },
  })

  return <>
    <SectionHeader title="Finance Posting Status" description="Operational-to-accounting status, explicit failure codes and governed replay using the immutable Finance-owned source snapshot." />
    <LoadingOrError pending={postings.isPending} error={postings.error} />
    {replay.error && <Alert severity="error" sx={{ mb: 2 }}>{replay.error.message}</Alert>}
    <Paper variant="outlined" sx={{ p: 2, overflowX: 'auto' }}>
      {postings.data && postings.data.length === 0 && <Alert severity="info">No Goods Receipt Finance source postings are available.</Alert>}
      {postings.data && postings.data.length > 0 && <Table size="small">
        <TableHead><TableRow><TableCell>Goods Receipt</TableCell><TableCell>Business Date</TableCell><TableCell>Status</TableCell><TableCell>Failure</TableCell><TableCell>Attempts</TableCell><TableCell>Journal</TableCell><TableCell>Action</TableCell></TableRow></TableHead>
        <TableBody>{postings.data.map(row => <TableRow key={row.id}>
          <TableCell>{row.goodsReceiptNumber}</TableCell>
          <TableCell>{row.businessDate}</TableCell>
          <TableCell><Chip size="small" label={row.status} color={row.status === 'POSTED' ? 'success' : row.status === 'FAILED' ? 'error' : 'default'} /></TableCell>
          <TableCell>{row.errorCode ?? '—'}</TableCell>
          <TableCell>{row.attemptCount} / replay {row.replayCount}</TableCell>
          <TableCell>{row.journalId ? <Button component={Link} to={`/finance/journals/${row.journalId}`} size="small">Open</Button> : '—'}</TableCell>
          <TableCell><Button size="small" variant="outlined" disabled={row.status !== 'FAILED' || replay.isPending} onClick={() => replay.mutate(row.id)}>Replay</Button></TableCell>
        </TableRow>)}</TableBody>
      </Table>}
    </Paper>
  </>
}

export function FinanceJournalsPage() {
  const journals = useQuery({
    queryKey: ['finance-journals'],
    queryFn: () => requireResult(apiClient.GET('/api/finance/journals', { params: { query: { limit: 100 } } })),
  })
  return <>
    <SectionHeader title="Finance Journals" description="Read-only posted accounting consequences with balanced totals and direct source traceability." />
    <LoadingOrError pending={journals.isPending} error={journals.error} />
    <Paper variant="outlined" sx={{ p: 2, overflowX: 'auto' }}>
      {journals.data && journals.data.length === 0 && <Alert severity="info">No posted Finance Journals are available.</Alert>}
      {journals.data && journals.data.length > 0 && <Table size="small">
        <TableHead><TableRow><TableCell>Journal</TableCell><TableCell>Goods Receipt</TableCell><TableCell>Business Date</TableCell><TableCell>Currency</TableCell><TableCell>Debit</TableCell><TableCell>Credit</TableCell><TableCell>Status</TableCell><TableCell /></TableRow></TableHead>
        <TableBody>{journals.data.map(row => <TableRow key={row.id}>
          <TableCell>{row.number}</TableCell>
          <TableCell>{row.goodsReceiptNumber}</TableCell>
          <TableCell>{row.businessDate}</TableCell>
          <TableCell>{row.currency}</TableCell>
          <TableCell>{row.totalDebit.toLocaleString()}</TableCell>
          <TableCell>{row.totalCredit.toLocaleString()}</TableCell>
          <TableCell><Chip size="small" label={row.status} color="success" /></TableCell>
          <TableCell><Button component={Link} to={`/finance/journals/${row.id}`} size="small">Open</Button></TableCell>
        </TableRow>)}</TableBody>
      </Table>}
    </Paper>
  </>
}

export function FinanceJournalDetailPage() {
  const { journalId = '' } = useParams()
  const journal = useQuery({
    queryKey: ['finance-journal', journalId],
    enabled: !!journalId,
    queryFn: () => requireResult(apiClient.GET('/api/finance/journals/{journalId}', { params: { path: { journalId } } })),
  })
  return <>
    <SectionHeader title="Finance Journal Detail" description="Immutable balanced Journal with Posting Rule, source event, Goods Receipt and line-level accounting traceability." />
    <LoadingOrError pending={journal.isPending} error={journal.error} />
    {journal.data && <Stack spacing={2}>
      <Paper variant="outlined" sx={{ p: 2 }}>
        <Stack direction={{ xs: 'column', md: 'row' }} spacing={2} sx={{ justifyContent: 'space-between' }}>
          <Box><Typography variant="h5">{journal.data.number}</Typography><Typography color="text.secondary">Goods Receipt {journal.data.goodsReceiptNumber} · Rule {journal.data.postingRuleCodeSnapshot} v{journal.data.postingRuleVersionNumber}</Typography></Box>
          <Chip label={journal.data.status} color="success" />
        </Stack>
        <Typography sx={{ mt: 2 }}>Business Date {journal.data.businessDate} · {journal.data.currency} · Debit {journal.data.totalDebit.toLocaleString()} · Credit {journal.data.totalCredit.toLocaleString()}</Typography>
        <Typography variant="body2" color="text.secondary" sx={{ mt: 1 }}>Source event {journal.data.sourceEventId} · Correlation {journal.data.correlationId}</Typography>
      </Paper>
      <Paper variant="outlined" sx={{ p: 2, overflowX: 'auto' }}><Table size="small">
        <TableHead><TableRow><TableCell>Line</TableCell><TableCell>Account</TableCell><TableCell>Debit</TableCell><TableCell>Credit</TableCell><TableCell>Source Amount</TableCell><TableCell>Description</TableCell></TableRow></TableHead>
        <TableBody>{journal.data.lines.map(line => <TableRow key={line.id}><TableCell>{line.lineNumber}</TableCell><TableCell>{line.accountCodeSnapshot} · {line.accountNameSnapshot}</TableCell><TableCell>{line.debitAmount || '—'}</TableCell><TableCell>{line.creditAmount || '—'}</TableCell><TableCell>{line.sourceLineAmount}</TableCell><TableCell>{line.description}</TableCell></TableRow>)}</TableBody>
      </Table></Paper>
      <Alert severity="info">Posted Finance Journals and Journal Lines are immutable. Corrections require separately governed reversal or adjustment functionality.</Alert>
    </Stack>}
  </>
}
