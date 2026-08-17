import {
  AppBar,
  Box,
  Chip,
  Container,
  Divider,
  List,
  ListItemButton,
  ListItemText,
  Paper,
  Stack,
  Toolbar,
  Typography,
} from '@mui/material'
import { BrowserRouter, NavLink, Navigate, Outlet, Route, Routes } from 'react-router'
import {
  CatalogPage,
  InventorySetupPage,
  PurchaseOrderDetailPage,
  PurchaseOrdersPage,
  SupplierOffersPage,
  SuppliersPage,
} from './pages/BatchCPages'
import { ApprovalInboxPage, ApprovalTaskPage, BatchDOverviewPage } from './pages/BatchDPages'

const navigation = [
  ['Overview', '/overview'],
  ['Catalog', '/catalog'],
  ['Suppliers', '/suppliers'],
  ['Supplier Offers', '/supplier-offers'],
  ['Purchase Orders', '/purchase-orders'],
  ['Approval Inbox', '/approvals'],
  ['Inventory Setup', '/inventory-setup'],
] as const

function ApplicationShell() {
  return (
    <Box sx={{ minHeight: '100vh', bgcolor: 'grey.50' }}>
      <AppBar position="static" elevation={0}>
        <Toolbar sx={{ gap: 2 }}>
          <Box sx={{ flexGrow: 1 }}>
            <Typography variant="h6" sx={{ fontWeight: 700 }}>OGFI Enterprise ERP</Typography>
            <Typography variant="caption" sx={{ opacity: 0.8 }}>
              RI-01 · Procure-to-Stock-to-Finance reference implementation
            </Typography>
          </Box>
          <Chip label="RI01-BL04 Candidate" size="small" variant="outlined" sx={{ color: 'inherit', borderColor: 'currentColor' }} />
          <Chip label="Batch D" size="small" color="secondary" />
        </Toolbar>
      </AppBar>

      <Container maxWidth="xl" sx={{ py: 3 }}>
        <Stack direction={{ xs: 'column', md: 'row' }} spacing={3} sx={{ alignItems: 'flex-start' }}>
          <Paper component="nav" variant="outlined" sx={{ width: { xs: '100%', md: 230 }, flexShrink: 0, overflow: 'hidden' }}>
            <Box sx={{ p: 2 }}>
              <Typography variant="overline" color="text.secondary">Reference workspace</Typography>
              <Typography variant="body2">Server-authoritative business operations</Typography>
            </Box>
            <Divider />
            <List disablePadding>
              {navigation.map(([label, to]) => (
                <ListItemButton
                  key={to}
                  component={NavLink}
                  to={to}
                  sx={{ '&.active': { bgcolor: 'action.selected', borderRight: 3, borderColor: 'primary.main' } }}
                >
                  <ListItemText primary={label} />
                </ListItemButton>
              ))}
            </List>
          </Paper>

          <Box component="main" sx={{ flex: 1, minWidth: 0, width: '100%' }}>
            <Outlet />
          </Box>
        </Stack>
      </Container>
    </Box>
  )
}

export function App() {
  return (
    <BrowserRouter>
      <Routes>
        <Route element={<ApplicationShell />}>
          <Route index element={<Navigate to="/overview" replace />} />
          <Route path="/overview" element={<BatchDOverviewPage />} />
          <Route path="/catalog" element={<CatalogPage />} />
          <Route path="/suppliers" element={<SuppliersPage />} />
          <Route path="/supplier-offers" element={<SupplierOffersPage />} />
          <Route path="/purchase-orders" element={<PurchaseOrdersPage />} />
          <Route path="/purchase-orders/:purchaseOrderId" element={<PurchaseOrderDetailPage />} />
          <Route path="/approvals" element={<ApprovalInboxPage />} />
          <Route path="/approvals/:taskId" element={<ApprovalTaskPage />} />
          <Route path="/inventory-setup" element={<InventorySetupPage />} />
          <Route path="*" element={<Navigate to="/overview" replace />} />
        </Route>
      </Routes>
    </BrowserRouter>
  )
}
