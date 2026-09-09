import { Component, OnInit, ChangeDetectorRef } from '@angular/core';
import { StatementService, StatementSummaryDto, StatementDetailDto } from '../../services/statement.service';

@Component({
  selector: 'app-files',
  templateUrl: './files.component.html',
  styleUrls: ['./files.component.css'],
  standalone: false
})
export class FilesComponent implements OnInit {
  searchQuery = '';
  statements: StatementSummaryDto[] = [];
  isLoading = true;
  errorMessage: string | null = null;

  // Statement Detail Modal State
  selectedDetail: StatementDetailDto | null = null;
  isLoadingDetail = false;
  detailError: string | null = null;

  constructor(
    public statementService: StatementService,
    private cdr: ChangeDetectorRef
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

  get filteredStatements(): StatementSummaryDto[] {
    if (!this.searchQuery.trim()) {
      return this.statements;
    }
    const q = this.searchQuery.toLowerCase().trim();
    return this.statements.filter(s =>
      s.originalFileName.toLowerCase().includes(q) ||
      (s.clientName && s.clientName.toLowerCase().includes(q)) ||
      s.status.toLowerCase().includes(q)
    );
  }

  downloadStatement(item: StatementSummaryDto | StatementDetailDto): void {
    this.statementService.downloadStatementFile(item.id, item.originalFileName);
  }

  viewDetails(id: string): void {
    this.isLoadingDetail = true;
    this.detailError = null;
    this.selectedDetail = null;
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
    this.cdr.markForCheck();
  }
}

