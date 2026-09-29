import { Component, OnInit, ChangeDetectorRef } from '@angular/core';
import { StatementService, StatementSummaryDto, StatementDetailDto } from '../../services/statement.service';

@Component({
  selector: 'app-files',
  templateUrl: './files.component.html',
  styleUrls: ['./files.component.css'],
  standalone: false
})
export class FilesComponent implements OnInit {
  statements: StatementSummaryDto[] = [];
  isLoading = true;
  errorMessage: string | null = null;

  // Search, Filter & Sort State
  searchQuery = '';
  selectedClientFilter = 'ALL';
  selectedFyFilter = 'ALL';
  selectedStatusFilter = 'ALL';
  sortBy: 'newest' | 'oldest' | 'name' | 'size' = 'newest';

  // Detail Modal State
  selectedDetail: StatementDetailDto | null = null;
  isLoadingDetail = false;
  detailError: string | null = null;
  detailIdCopied = false;

  // Action Loading States
  isDownloading: { [id: string]: boolean } = {};
  isExporting: { [id: string]: boolean } = {};
  isDeleting: { [id: string]: boolean } = {};

  constructor(
    public statementService: StatementService,
    public cdr: ChangeDetectorRef
  ) {}

  ngOnInit(): void {
    this.loadStatements();
  }

  loadStatements(): void {
    this.isLoading = true;
    this.errorMessage = null;
    this.cdr.markForCheck();

    this.statementService.getStatements().subscribe({
      next: (data) => {
        this.statements = data || [];
        this.isLoading = false;
        this.cdr.markForCheck();
      },
      error: (_err) => {
        this.isLoading = false;
        this.errorMessage = 'Failed to load statements from database. Please verify backend connection.';
        this.cdr.markForCheck();
      }
    });
  }

  // Dynamic Filter Options extracted from loaded data
  get availableClients(): string[] {
    const clients = new Set<string>();
    for (const s of this.statements) {
      clients.add(s.clientName || 'Default Workspace');
    }
    return Array.from(clients).sort((a, b) => a.localeCompare(b));
  }

  get availableFinancialYears(): string[] {
    const years = new Set<string>();
    for (const s of this.statements) {
      if (s.financialYearName) {
        years.add(s.financialYearName);
      }
    }
    return Array.from(years).sort((a, b) => b.localeCompare(a));
  }

  // Workspace Summary KPI Metrics
  get totalStatementsCount(): number {
    return this.statements.length;
  }

  get completedStatementsCount(): number {
    return this.statements.filter(s => s.processingStatus === 2).length;
  }

  get inProgressStatementsCount(): number {
    return this.statements.filter(s => s.processingStatus === 0 || s.processingStatus === 1).length;
  }

  get failedStatementsCount(): number {
    return this.statements.filter(s => s.processingStatus === 3 || s.processingStatus < 0).length;
  }

  get hasActiveFilters(): boolean {
    return (
      this.searchQuery.trim().length > 0 ||
      this.selectedClientFilter !== 'ALL' ||
      this.selectedFyFilter !== 'ALL' ||
      this.selectedStatusFilter !== 'ALL' ||
      this.sortBy !== 'newest'
    );
  }

  clearFilters(): void {
    this.searchQuery = '';
    this.selectedClientFilter = 'ALL';
    this.selectedFyFilter = 'ALL';
    this.selectedStatusFilter = 'ALL';
    this.sortBy = 'newest';
    this.cdr.markForCheck();
  }

  // Multi-dimensional Filter & Sort Pipeline
  get filteredStatements(): StatementSummaryDto[] {
    let result = [...this.statements];

    // 1. Text Search Filter
    if (this.searchQuery.trim()) {
      const q = this.searchQuery.toLowerCase().trim();
      result = result.filter(s =>
        s.originalFileName.toLowerCase().includes(q) ||
        (s.clientName && s.clientName.toLowerCase().includes(q)) ||
        (s.financialYearName && s.financialYearName.toLowerCase().includes(q)) ||
        s.status.toLowerCase().includes(q) ||
        s.id.toLowerCase().includes(q)
      );
    }

    // 2. Client Filter
    if (this.selectedClientFilter !== 'ALL') {
      result = result.filter(s => (s.clientName || 'Default Workspace') === this.selectedClientFilter);
    }

    // 3. Financial Year Filter
    if (this.selectedFyFilter !== 'ALL') {
      result = result.filter(s => s.financialYearName === this.selectedFyFilter);
    }

    // 4. Status Filter
    if (this.selectedStatusFilter !== 'ALL') {
      if (this.selectedStatusFilter === 'COMPLETED') {
        result = result.filter(s => s.processingStatus === 2);
      } else if (this.selectedStatusFilter === 'PROCESSING') {
        result = result.filter(s => s.processingStatus === 0 || s.processingStatus === 1);
      } else if (this.selectedStatusFilter === 'FAILED') {
        result = result.filter(s => s.processingStatus === 3 || s.processingStatus < 0);
      }
    }

    // 5. Sorting
    result.sort((a, b) => {
      switch (this.sortBy) {
        case 'oldest':
          return new Date(a.uploadedAt).getTime() - new Date(b.uploadedAt).getTime();
        case 'name':
          return a.originalFileName.localeCompare(b.originalFileName);
        case 'size':
          return b.fileSizeBytes - a.fileSizeBytes;
        case 'newest':
        default:
          return new Date(b.uploadedAt).getTime() - new Date(a.uploadedAt).getTime();
      }
    });

    return result;
  }

  // Actions
  downloadStatement(item: StatementSummaryDto | StatementDetailDto): void {
    if (this.isDownloading[item.id]) return;
    this.isDownloading[item.id] = true;
    this.cdr.markForCheck();

    this.statementService.downloadStatement(item.id).subscribe({
      next: (blob: Blob) => {
        const url = window.URL.createObjectURL(blob);
        const a = document.createElement('a');
        a.href = url;
        a.download = item.originalFileName;
        document.body.appendChild(a);
        a.click();
        document.body.removeChild(a);
        window.URL.revokeObjectURL(url);
        this.isDownloading[item.id] = false;
        this.cdr.markForCheck();
      },
      error: (_err: unknown) => {
        this.isDownloading[item.id] = false;
        this.cdr.markForCheck();
      }
    });
  }

  exportExcel(item: StatementSummaryDto | StatementDetailDto): void {
    if (this.isExporting[item.id] || item.processingStatus !== 2) return;
    this.isExporting[item.id] = true;
    this.cdr.markForCheck();

    this.statementService.exportExcel(item.id).subscribe({
      next: (blob: Blob) => {
        const url = window.URL.createObjectURL(blob);
        const a = document.createElement('a');
        a.href = url;
        const cleanName = item.originalFileName.replace(/\.[^/.]+$/, '');
        a.download = `${cleanName}_Ledger.xlsx`;
        document.body.appendChild(a);
        a.click();
        document.body.removeChild(a);
        window.URL.revokeObjectURL(url);
        this.isExporting[item.id] = false;
        this.cdr.markForCheck();
      },
      error: (_err: unknown) => {
        this.isExporting[item.id] = false;
        this.cdr.markForCheck();
      }
    });
  }

  deleteStatement(item: StatementSummaryDto | StatementDetailDto): void {
    if (this.isDeleting[item.id]) return;

    const confirmed = window.confirm(`Are you sure you want to delete "${item.originalFileName}"? This will permanently remove the physical file and all associated transactions.`);
    if (!confirmed) return;

    this.isDeleting[item.id] = true;
    this.cdr.markForCheck();

    this.statementService.deleteStatement(item.id).subscribe({
      next: () => {
        this.statements = this.statements.filter(s => s.id !== item.id);
        delete this.isDeleting[item.id];
        if (this.selectedDetail?.id === item.id) {
          this.closeDetails();
        }
        this.cdr.markForCheck();
      },
      error: (_err: unknown) => {
        this.isDeleting[item.id] = false;
        alert('Failed to delete statement. It may have already been removed or you are not authorized.');
        this.cdr.markForCheck();
      }
    });
  }

  viewDetails(id: string): void {
    this.isLoadingDetail = true;
    this.detailError = null;
    this.selectedDetail = null;
    this.detailIdCopied = false;
    this.cdr.markForCheck();

    this.statementService.getStatementById(id).subscribe({
      next: (detail: StatementDetailDto) => {
        this.selectedDetail = detail;
        this.isLoadingDetail = false;
        this.cdr.markForCheck();
      },
      error: (_err: unknown) => {
        this.isLoadingDetail = false;
        this.detailError = 'Could not load statement details.';
        this.cdr.markForCheck();
      }
    });
  }

  closeDetails(): void {
    this.selectedDetail = null;
    this.detailError = null;
    this.isLoadingDetail = false;
    this.detailIdCopied = false;
    this.cdr.markForCheck();
  }

  copyDetailId(): void {
    if (!this.selectedDetail?.id) return;
    const id = this.selectedDetail.id;

    if (navigator?.clipboard?.writeText) {
      navigator.clipboard.writeText(id).catch(() => {});
    }
    this.detailIdCopied = true;
    setTimeout(() => {
      this.detailIdCopied = false;
      this.cdr.markForCheck();
    }, 2000);
    this.cdr.markForCheck();
  }
}
